using System.Buffers.Binary;

namespace Shenora.Tests.TestSupport;

/// <summary>
/// An ISO-BMFF box navigator for tests — the ONE owner, used by the whole-file writer's tests and the
/// FRAGMENT writer's alike.
/// <para>
/// ⚠ It lived as a private block inside <c>Mp4RemuxerTests</c>, whose comment said only the FIXTURE BUILDER
/// was shared and "the box navigator below stays private". That was scope at the time, not a principle: the
/// moment a second suite had to walk boxes, the choice was one navigator or two, and two readers of the same
/// format drift exactly like the two fixture builders that comment was written to prevent. Moved verbatim
/// apart from the <c>traf</c> line noted below.
/// </para>
/// </summary>
internal static class Mp4Boxes
{
    /// <summary>
    /// Boxes directly inside a payload: a 4-byte length, a 4-character type, then the body.
    /// <para>
    /// ⚠ A declared size of 1 means the REAL size is the eight bytes after the type — the 64-bit form, which
    /// the media box takes once a file passes 4 GB. This navigator started without that case and reported a
    /// perfectly good file as having no media box at all, which is worth keeping as a comment: a reader that
    /// only handles the common header is how "the output is truncated" gets misdiagnosed.
    /// </para>
    /// </summary>
    public static List<(string Type, byte[] Payload)> Children(byte[] data, int skip = 0)
    {
        var found = new List<(string, byte[])>();
        var at = skip;
        while (at + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
            var type = System.Text.Encoding.ASCII.GetString(data, at + 4, 4);
            var header = 8;

            if (size == 1)
            {
                if (at + 16 > data.Length) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8, 8));
                header = 16;
            }

            if (size < header || at + size > data.Length) break;
            found.Add((type, data[(at + header)..(int)(at + size)]));
            at += (int)size;
        }
        return found;
    }

    /// <summary>Containers that are FULL boxes — their children start after a version/flags word (and, for
    /// <c>stsd</c>, an entry count too). Getting this wrong finds no children at all.</summary>
    public static int SkipFor(string type) => type switch { "stsd" => 8, _ => 0 };

    /// <summary>
    /// Follow a slash-separated box path, e.g. <c>moov/trak/mdia/minf/stbl/stsz</c>.
    /// <para>
    /// ⚠ <c>traf</c> indexes with <paramref name="trackIndex"/> exactly as <c>trak</c> does — the one change
    /// made when this moved. A fragment carries one <c>traf</c> per contributing track in the same order the
    /// init segment declared them, so without it every assertion about a fragment's SECOND track would
    /// silently read its first.
    /// </para>
    /// </summary>
    public static byte[]? Find(byte[] data, string path, int trackIndex = 0)
    {
        var current = data;
        var skip = 0;
        foreach (var step in path.Split('/'))
        {
            var children = Children(current, skip);
            var matches = children.Where(c => c.Type == step).ToList();
            if (matches.Count == 0) return null;
            current = step is "trak" or "traf" ? matches[trackIndex].Payload : matches[0].Payload;
            skip = SkipFor(step);
        }
        return current;
    }

    public static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));

    public static ulong U64(byte[] data, int at) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at, 8));
}
