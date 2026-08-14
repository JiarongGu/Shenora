using System.Reflection;
using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

public class MediaAccessOptionsTests
{
    /// <summary>
    /// 🔴 Containment is stated ONCE. Three options types used to declare their own AllowedRoots and
    /// CacheRoot, which is three places for a security boundary to drift — and D71 adds a delivery path
    /// that would have made it four. This fails the build rather than trusting a convention.
    /// </summary>
    [Fact]
    public void No_media_options_type_declares_its_own_containment()
    {
        var offenders = typeof(MediaAccessOptions).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "Shenora.Modules.Media" && t.Name.EndsWith("Options"))
            .Where(t => t != typeof(MediaAccessOptions))
            .SelectMany(t => new[] { "AllowedRoots", "CacheRoot" }
                .Where(p => t.GetProperty(p, BindingFlags.Public | BindingFlags.Instance) is not null)
                .Select(p => $"{t.Name}.{p}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These declare containment beside MediaAccessOptions: {string.Join(", ", offenders)}");
    }
}
