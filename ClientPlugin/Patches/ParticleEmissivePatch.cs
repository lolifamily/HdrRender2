using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.VRage.Render.Data;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.SceneSystem.Components;
using Keen.VRage.Render12.SceneSystem.GPU;
using Keen.VRage.Render12.TransparentStage;

namespace ClientPlugin.Patches;

// Emissive particle boost + live preview.
//
// [boost] SE2 has no CPU billboards -- thruster flames/lasers/muzzle flashes/explosions are all GPU particles.
// Each emitter's Emissivity curve is normalized and its peak packed separately as fp16 (low 16 bits of
// GPUTimeVaryingFloat2D.PackedValueMultiplierAndKey1); the shader samples normalized-curve x peak, with final
// output = color*(volumeLighting*intensity + emissivity). So boost = peak x K. We boost only Emissivity (pure
// additive self-glow): Emissivity=0 diffuse smoke is naturally unaffected, no threshold needed. Intensity untouched.
//
// [why hook BuildGPUParticleEmitter, not UploadEmitterData] UploadEmitterData is a 10-line method called from
// exactly one site (UpdateEmitters), so the JIT inlines it into UpdateEmitters, the method entry never executes,
// and a Harmony patch never fires. BuildGPUParticleEmitter is a 100+ line encoding
// method, not inlined, and the GPUParticleEmitter it produces is exactly the one uploaded next -- patching its
// Postfix to edit __result makes the boost reliably take effect.
//
// [live preview: partial re-upload] The boost is baked once at upload time; changing K requires re-upload. A full
// UpdateEmitters would ForceReleaseAll + re-encode every whole curve, while we only changed one fp16 peak. So:
// at BuildGPUParticleEmitter, cache [original peak + its 256B subset] per definition; when K changes, write only
// original-peak x K back into that subset and ScheduleUpdate that single subset (no ForceReleaseAll, no re-encode).
// On re-upload, get each definition's GPU index from ParticleEffectManager._emitters.
//
// [concurrency] Cache / _appliedK are touched only by the render thread (BuildGPUParticleEmitter via UpdateEmitters,
// and DoWork, both on the render thread, serially). Whether K changed is compared each frame against the
// render-private _appliedK: no cross-thread dirty flag, no race; the only cross-thread state is the particleBoost
// float, atomic to read/write, worst case taking effect a few frames late -- imperceptible in preview.
//
// publicizer has opened up VRage.Render12, so the relevant internal/private members are directly accessible.
[HarmonyPatch]
internal static class ParticleEmissivePatch
{
    private const float Fp16Max = 65504f; // largest finite fp16 value; guards against K*peak overflowing to Inf

    private struct Slot
    {
        public int SubsetIdx;        // which 256B subset (0..3) the Emissivity peak lands in
        public int PeakOffInSubset;  // byte offset of the peak uint within that subset
        public float OrigPeak;       // original pre-boost peak (multiply it by K each time, never accumulate)
        public ParticleEffectManagerComponent.GPUParticleEmitterSubset OrigSubset; // the full 256B of that subset before boost
    }

    private static readonly Dictionary<ParticleEmitterDefinition, Slot> Cache = new();
    private static int _peakByteOffset = -1;     // offset of Emissivity.PackedValueMultiplierAndKey1 from the emitter start
    private static float _appliedK = float.NaN;  // K last applied to the GPU (read/written only on the render thread)

    // Byte offset of the Emissivity peak -- computed once at runtime via Unsafe, not by hand-calculating the struct layout.
    private static unsafe int PeakByteOffset()
    {
        if (_peakByteOffset >= 0) return _peakByteOffset;
        GPUParticleEmitter e = default;
        var b = (byte*)Unsafe.AsPointer(ref e);
        var p = (byte*)Unsafe.AsPointer(ref e.Emissivity.PackedValueMultiplierAndKey1);
        _peakByteOffset = (int)(p - b);
        return _peakByteOffset;
    }

    // Low 16 bits of packed = fp16 peak; returns a new packed with min(origPeak*k, fp16max), preserving the high 16 bits (key1). k<=1 unchanged.
    private static uint ScalePacked(uint packedOrig, float origPeak, float k)
    {
        if (k <= 1f || origPeak <= 0f) return packedOrig;
        var bits = BitConverter.HalfToUInt16Bits((Half)Math.Min(origPeak * k, Fp16Max));
        return (packedOrig & 0xFFFF0000u) | bits;
    }

    // When encoding an emitter template: cache the original peak + its subset per definition, and boost the data produced this pass.
    [HarmonyPatch(typeof(ParticleEmitterDefinitionExtention), nameof(ParticleEmitterDefinitionExtention.BuildGPUParticleEmitter))]
    [HarmonyPostfix]
    private static unsafe void BoostAndCache(ParticleEmitterDefinition emitterDefinition, ref GPUParticleEmitter __result)
    {
        var off = PeakByteOffset();
        var subsetIdx = off / 256;
        var packed = __result.Emissivity.PackedValueMultiplierAndKey1;
        var origPeak = (float)BitConverter.UInt16BitsToHalf((ushort)(packed & 0xFFFF));

        var baseP = (byte*)Unsafe.AsPointer(ref __result);
        Cache[emitterDefinition] = new Slot
        {
            SubsetIdx = subsetIdx,
            PeakOffInSubset = off % 256,
            OrigPeak = origPeak,
            // Dereference-assign: cache this subset before boost (original peak + other fields). Writing OrigSubset= explicitly so the compiler treats it as an assignment.
            OrigSubset = *(ParticleEffectManagerComponent.GPUParticleEmitterSubset*)(baseP + subsetIdx * 256)
        };

        __result.Emissivity.PackedValueMultiplierAndKey1 = ScalePacked(packed, origPeak, Config.Current.ParticleBoost);
    }

    // Every render frame (before particle processing): if K changed since last time, write the new peak back into each emitter's Emissivity subset, pushing only that one subset.
    [HarmonyPatch(typeof(ParticleEmitterProcessingJob), nameof(ParticleEmitterProcessingJob.DoWork))]
    [HarmonyPrefix]
    private static unsafe void ReuploadIfChanged()
    {
        var k = Config.Current.ParticleBoost;
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (k == _appliedK) return;
        _appliedK = k;

        var pem = CoreSystems.ParticleEffectManager;
        var ped = CoreSystems.GPUScene.ParticleEmitterEntityData;

        foreach (var kv in pem._emitters) // KeyValuePair<ReferenceEquatable<ParticleEmitterDefinition>, int>
        {
            if (!Cache.TryGetValue(kv.Key.Value, out var slot)) continue;
            var sub = slot.OrigSubset; // start from the original subset, change only the 2 peak bytes, leave the rest
            var peakPtr = (byte*)Unsafe.AsPointer(ref sub) + slot.PeakOffInSubset;
            *(uint*)peakPtr = ScalePacked(*(uint*)peakPtr, slot.OrigPeak, k);
            ped.ScheduleUpdate(kv.Value * 4 + slot.SubsetIdx, sub);
        }
    }
}
