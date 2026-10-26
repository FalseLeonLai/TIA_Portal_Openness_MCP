#if TIA_V19
namespace Siemens.Engineering.SW.Blocks
{
    /// <summary>
    /// TIA Portal V19 does not expose ImportDocumentOptions (SIMATIC SD import was added in V20).
    /// This shim only keeps shared method signatures compiling; every document-import path is
    /// guarded by Capability.RequireSupported(TiaFeature.DocumentExport) at runtime, so the
    /// values are never passed to Openness on V19.
    /// </summary>
    public enum ImportDocumentOptions
    {
        None = 0,
        Override = 1,
        SkipInactiveCultures = 2,
        ActivateInactiveCultures = 3
    }
}
#endif
