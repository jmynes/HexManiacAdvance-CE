using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HavenSoft.HexManiac.Mcp;

/// <summary>
/// Exposes the embedded MCP guide as an MCP resource at hexmaniac://guide.
/// Clients can discover it via resources/list and fetch it via resources/read.
/// </summary>
[McpServerResourceType]
public static class HelpResources {
   [McpServerResource(
      UriTemplate = "hexmaniac://guide",
      Name        = "HexManiacAdvance MCP guide",
      MimeType    = "text/markdown")]
   public static TextResourceContents GetGuide() => new() {
      Uri      = "hexmaniac://guide",
      MimeType = "text/markdown",
      Text     = Docs.Guide(),
   };
}
