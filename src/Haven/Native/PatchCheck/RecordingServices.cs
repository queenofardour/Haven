using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Haven.Native.PatchCheck;

/// <summary>Dalamud's signature scanner, noting every lookup and its answer in <see cref="PatchReport"/>.</summary>
internal sealed class RecordingSigScanner(ISigScanner inner) : ISigScanner
{
    public bool IsCopy => inner.IsCopy;
    public nint SearchBase => inner.SearchBase;
    public nint TextSectionBase => inner.TextSectionBase;
    public long TextSectionOffset => inner.TextSectionOffset;
    public int TextSectionSize => inner.TextSectionSize;
    public nint DataSectionBase => inner.DataSectionBase;
    public long DataSectionOffset => inner.DataSectionOffset;
    public int DataSectionSize => inner.DataSectionSize;
    public nint RDataSectionBase => inner.RDataSectionBase;
    public long RDataSectionOffset => inner.RDataSectionOffset;
    public int RDataSectionSize => inner.RDataSectionSize;
    public ProcessModule Module => inner.Module;

    private static T Throwing<T>(string kind, string signature, Func<T> scan)
    {
        try { var result = scan(); PatchReport.Record(kind, signature, true, null); return result; }
        catch (Exception ex) { PatchReport.Record(kind, signature, false, ex.Message); throw; }
    }

    private static bool Trying(string kind, string signature, bool found)
    {
        PatchReport.Record(kind, signature, found, found ? null : "not found");
        return found;
    }

    public nint ScanText(string signature) => Throwing("text", signature, () => inner.ScanText(signature));
    public bool TryScanText(string signature, out nint result) => Trying("text", signature, inner.TryScanText(signature, out result));
    public nint ScanData(string signature) => Throwing("data", signature, () => inner.ScanData(signature));
    public bool TryScanData(string signature, out nint result) => Trying("data", signature, inner.TryScanData(signature, out result));
    public nint ScanModule(string signature) => Throwing("module", signature, () => inner.ScanModule(signature));
    public bool TryScanModule(string signature, out nint result) => Trying("module", signature, inner.TryScanModule(signature, out result));
    public nint GetStaticAddressFromSig(string signature, int offset = 0) => Throwing("static", signature, () => inner.GetStaticAddressFromSig(signature, offset));
    public bool TryGetStaticAddressFromSig(string signature, out nint result, int offset = 0)
        => Trying("static", signature, inner.TryGetStaticAddressFromSig(signature, out result, offset));
    public nint ResolveRelativeAddress(nint nextInstAddr, int relOffset) => inner.ResolveRelativeAddress(nextInstAddr, relOffset);
    public nint[] ScanAllText(string signature) => Throwing("all", signature, () => inner.ScanAllText(signature));
    public IEnumerable<nint> ScanAllText(string signature, CancellationToken cancellationToken) => inner.ScanAllText(signature, cancellationToken);
}

/// <summary>Dalamud's hook maker, noting every hook made from a signature (and any that fails) in <see cref="PatchReport"/>.</summary>
internal sealed class RecordingInterop(IGameInteropProvider inner) : IGameInteropProvider
{
    public Hook<T> HookFromSignature<T>(string signature, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate
    {
        try { var hook = inner.HookFromSignature(signature, detour, backend); PatchReport.Record("hook", signature, true, null); return hook; }
        catch (Exception ex) { PatchReport.Record("hook", signature, false, ex.Message); throw; }
    }

    public Hook<T> HookFromAddress<T>(nint procAddress, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate
        => Address(() => inner.HookFromAddress(procAddress, detour, backend));
    public Hook<T> HookFromAddress<T>(nuint procAddress, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate
        => Address(() => inner.HookFromAddress(procAddress, detour, backend));
    public unsafe Hook<T> HookFromAddress<T>(void* procAddress, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate
    {
        var address = (nint)procAddress;
        return Address(() => inner.HookFromAddress(address, detour, backend));
    }

    // Hooks on addresses (ClientStructs' functions, or ones a signature found) are noted only when they fail.
    private static Hook<T> Address<T>(Func<Hook<T>> make) where T : Delegate
    {
        try { return make(); }
        catch (Exception ex) { PatchReport.Record("address hook", typeof(T).Name, false, ex.Message); throw; }
    }

    public Hook<T> HookFromFunctionPointerVariable<T>(nint address, T detour) where T : Delegate => inner.HookFromFunctionPointerVariable(address, detour);
    public Hook<T> HookFromImport<T>(ProcessModule? module, string moduleName, string functionName, uint hintOrOrdinal, T detour) where T : Delegate
        => inner.HookFromImport(module, moduleName, functionName, hintOrOrdinal, detour);
    public Hook<T> HookFromSymbol<T>(string moduleName, string exportName, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate
        => inner.HookFromSymbol(moduleName, exportName, detour, backend);
    public void InitializeFromAttributes(object self) => inner.InitializeFromAttributes(self);
}
