using Shenora.Core.Ipc;


namespace Shenora.Sample.Maui;

/// <summary>
/// A mobile-only diagnostic sink: the PAGE sends its own log lines here and the host writes them to the
/// device log, so page state can be read as TEXT (<c>dev.mjs mac log</c> / <c>android log</c>) instead of
/// off a screenshot.
/// <para>
/// It exists because the two cheaper routes are both closed. `mac safari-eval` would be the general
/// answer, and it needs a Homebrew package that will not install on this build Mac (the trail is in
/// `local/MAC-DIAGNOSTICS.md`). And WebKit does NOT forward a page's <c>console.log</c> to the unified
/// log — measured on the simulator with a tagged line and zero hits, which is the whole reason this class
/// is here rather than a one-line mirror in the page.
/// </para>
/// <para>
/// Why this matters at all: a screenshot cannot report a number, a header or an array, and a
/// <c>&lt;video&gt;</c> element reports only "no supported source" however it actually failed. Reading
/// those off pixels is what made the DM1 media work (D44) slow.
/// </para>
/// <para>
/// SAMPLE-LOCAL on purpose. It is not kit surface and not portable logic: a kit that logged every inbound
/// page message by default would be both noisy and a privacy hazard, and the sample is the only thing
/// whose page we control. An adopter that wants this writes the same twenty lines.
/// </para>
/// </summary>
public sealed class PageDiagModule : ModuleBase
{
	/// <summary>The reserved module name for page→host diagnostics.</summary>
	public const string Module = "PAGE_DIAG";

	/// <inheritdoc />
	public override string ModuleName => Module;

	/// <inheritdoc />
	protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context,
													   CancellationToken cancellationToken)
	{
		switch (request.Type)
		{
			case "LOG":
				// Optional, and deliberately not `GetRequiredValue`: a diagnostic channel that THROWS on a
				// malformed diagnostic turns a logging problem into an error-boundary problem, and the
				// thing being diagnosed is usually already broken.
				var text = PayloadHelper.GetOptionalValue<string>(request.Payload, "text") ?? "(no text)";
				MauiProgram.Log($"[PAGE] {text}");
				return Done();

			default:
				throw UnknownType(request);
		}
	}
}
