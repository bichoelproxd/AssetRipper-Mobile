using AssetRipper.NativeDialogs;
using AssetRipper.Web.Extensions;
using Microsoft.AspNetCore.Http;

namespace AssetRipper.GUI.Web;

internal static class Dialogs
{
	/// <summary>
	/// Whether the desktop native dialogs can be used. Always false on Android.
	/// </summary>
	public static bool NativeSupported => !OperatingSystem.IsAndroid() && NativeDialog.Supported;

	public static bool Supported => NativeSupported || PlatformDialogHandlers.OpenFile is not null;

	private static async Task<string?> PickFile() => PlatformDialogHandlers.OpenFile is { } h ? await h() : await OpenFileDialog.OpenFile();
	private static async Task<string?> PickFolder() => PlatformDialogHandlers.OpenFolder is { } h ? await h() : await OpenFolderDialog.OpenFolder();
	private static async Task<string?> PickSave() => PlatformDialogHandlers.SaveFolder is { } h ? await h() : await SaveFileDialog.SaveFile();

	public static class OpenFiles
	{
		public static async Task HandleGetRequest(HttpContext context)
		{
			context.Response.DisableCaching();
			string[] paths = (PlatformDialogHandlers.OpenFile is { } h ? await h() is { } one ? [one] : null : await OpenFileDialog.OpenFiles()) ?? [];
			await Results.Json(paths, AppJsonSerializerContext.Default.StringArray).ExecuteAsync(context);
		}
	}

	public static class OpenFolders
	{
		public static async Task HandleGetRequest(HttpContext context)
		{
			context.Response.DisableCaching();
			string[] paths = (PlatformDialogHandlers.OpenFolder is { } h ? await h() is { } one ? [one] : null : await OpenFolderDialog.OpenFolders()) ?? [];
			await Results.Json(paths, AppJsonSerializerContext.Default.StringArray).ExecuteAsync(context);
		}
	}

	public static class OpenFile
	{
		public static async Task HandleGetRequest(HttpContext context)
		{
			context.Response.DisableCaching();
			string path = await PickFile() ?? "";
			await Results.Json(path, AppJsonSerializerContext.Default.String).ExecuteAsync(context);
		}
	}

	public static class OpenFolder
	{
		public static async Task HandleGetRequest(HttpContext context)
		{
			context.Response.DisableCaching();
			string path = await PickFolder() ?? "";
			await Results.Json(path, AppJsonSerializerContext.Default.String).ExecuteAsync(context);
		}
	}

	public static class SaveFile
	{
		public static async Task HandleGetRequest(HttpContext context)
		{
			context.Response.DisableCaching();
			string path = await PickSave() ?? "";
			await Results.Json(path, AppJsonSerializerContext.Default.String).ExecuteAsync(context);
		}
	}
}

/// <summary>
/// Lets a host application (for example Android) replace the desktop native dialogs.
/// </summary>
public static class PlatformDialogHandlers
{
	/// <summary>Returns a readable local file path, or null if cancelled.</summary>
	public static Func<Task<string?>>? OpenFile { get; set; }
	/// <summary>Returns a readable local folder path, or null if cancelled.</summary>
	public static Func<Task<string?>>? OpenFolder { get; set; }
	/// <summary>Returns a writable local folder path to export into, or null if cancelled.</summary>
	public static Func<Task<string?>>? SaveFolder { get; set; }
}
