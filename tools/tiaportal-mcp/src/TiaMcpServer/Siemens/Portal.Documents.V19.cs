#if TIA_V19
using System.Collections.Generic;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// V19 stubs for the SIMATIC SD document API. TIA Portal V19 exposes neither
    /// PlcBlock.ExportAsDocuments nor PlcBlockComposition.ImportFromDocuments (both arrived in V20),
    /// so the real implementations are compiled out in Portal.cs under #if !TIA_V19.
    /// These stubs keep the MCP tool surface stable and fail with the same explicit
    /// NotSupportedOnVersion error the capability system uses at runtime.
    /// </summary>
    public partial class Portal
    {
        public bool ExportAsDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            Capability.RequireSupported(TiaFeature.DocumentExport);
            return false;
        }

        public IEnumerable<PlcBlock>? ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            Capability.RequireSupported(TiaFeature.DocumentExport);
            return null;
        }

        public bool ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, ImportDocumentOptions option)
        {
            Capability.RequireSupported(TiaFeature.DocumentExport);
            return false;
        }

        public IEnumerable<PlcBlock>? ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, ImportDocumentOptions option, bool preservePath = false)
        {
            Capability.RequireSupported(TiaFeature.DocumentExport);
            return null;
        }
    }
}
#endif
