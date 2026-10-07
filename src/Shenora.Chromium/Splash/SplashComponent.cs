using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Chromium;

/// <summary>
/// A splash: the setup runs once, as the splash starts, and returns the render function, which runs again whenever a
/// <see cref="SplashState{T}"/> it reads changes. The setup wires things and returns; the app's boot work goes in
/// <see cref="SplashContext.OnShown"/>.
/// </summary>
/// <param name="context">State, the app's services, and the events a splash may hook.</param>
/// <returns>The render function: the tree to draw now.</returns>
public delegate Func<SplashElement> SplashComponent(SplashContext context);

/// <summary>A splash as a class: the same setup as a <see cref="SplashComponent"/>, with its constructor's services
/// injected. Pass it as <see cref="Splash.Of{T}"/>.</summary>
public interface ISplashComponent
{
    /// <summary>Runs once, as the splash starts.</summary>
    /// <param name="context">State, the app's services, and the events a splash may hook.</param>
    /// <returns>The render function: the tree to draw now.</returns>
    Func<SplashElement> Setup(SplashContext context);
}

/// <summary>Ready-made <see cref="SplashComponent"/>s.</summary>
public static class Splash
{
    /// <summary>The class <typeparamref name="T"/>, made with the app's services as its constructor asks (it need not
    /// be registered), then set up.</summary>
    /// <typeparam name="T">The splash class.</typeparam>
    public static SplashComponent Of<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : ISplashComponent =>
        context => ActivatorUtilities.CreateInstance<T>(context.Services).Setup(context);

    /// <summary>The kit's default: an image and a title, centred, over a bar that slides while the app starts.</summary>
    /// <param name="image">A PNG, drawn 72 high; null for none.</param>
    /// <param name="title">The title; null for none.</param>
    public static SplashComponent Preset(string? image = null, string? title = null)
    {
        List<SplashElement> children = [];
        if (image is not null) children.Add(new SplashImage(image) { Height = 72 });
        if (title is not null) children.Add(new SplashText(title) { FontSize = 20, Bold = true });
        children.Add(new SplashProgress { Width = 240, Height = 3 });
        var tree = new SplashStack { Spacing = 18, Children = children };
        return _ => () => tree;
    }
}
