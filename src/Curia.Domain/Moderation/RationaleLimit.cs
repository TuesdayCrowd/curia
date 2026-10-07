using System.Text;

namespace Curia.Domain.Moderation;

public static class RationaleLimit
{
    /// R10.68 (errata G18). Measured in UTF-8 bytes; checked after authorization, before screening, before the post is looked up, before any write.
    public const int MaxUtf8Bytes = 4_096;
    /// The rationale's UTF-8 length when it is over the cap, or null.
    public static int? Over(string rationale) { var n = Encoding.UTF8.GetByteCount(rationale); return n > MaxUtf8Bytes ? n : null; }
}
