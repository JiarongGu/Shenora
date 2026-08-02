using UIKit;

namespace Shenora.Sample.Maui;

/// <summary>
/// iOS has an explicit <c>Main</c> where Android has an <c>[Application]</c> attribute — the one
/// structural difference between the two heads.
/// </summary>
public class Program
{
	// This is the main entry point of the application.
	static void Main(string[] args)
	{
		// If you want to use a different Application Delegate class from "AppDelegate"
		// you can specify it here.
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
