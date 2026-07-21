using System.Text;

namespace AgenticRag.Utils;

public static class ChunkingUtils
{
    public static string SafeKey(string blobName, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{blobName}::{index}"))
            .Replace('+', '-').Replace('/', '_');
}
