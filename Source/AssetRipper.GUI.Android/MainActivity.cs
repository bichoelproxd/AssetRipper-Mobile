using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Text.Method;
using Android.Util;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AUri = Android.Net.Uri;
using Path = System.IO.Path;

namespace AssetRipper.Mobile;

[Application(UsesCleartextTraffic = true, LargeHeap = true, Label = "AssetRipper")]
public class MobileApplication : Application
{
	public MobileApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer)
	{
	}
}

[Activity(
	Label = "AssetRipper",
	MainLauncher = true,
	Exported = true,
	Theme = "@android:style/Theme.Material.NoActionBar",
	ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/vnd.android.package-archive")]
public class MainActivity : Activity
{
	private const int PickFilesRequest = 1001;

	private static readonly Color Background = Color.ParseColor("#101418");
	private static readonly Color Surface = Color.ParseColor("#1B2128");
	private static readonly Color Accent = Color.ParseColor("#3DDC84");
	private static readonly Color TextPrimary = Color.ParseColor("#E6EAF0");
	private static readonly Color TextMuted = Color.ParseColor("#8C97A6");

	private FrameLayout root = null!;
	private ScrollView home = null!;
	private LinearLayout webContainer = null!;
	private WebView webView = null!;
	private TextView statusText = null!;
	private ProgressBar progress = null!;
	private TextView logText = null!;
	private ScrollView logScroll = null!;
	private LinearLayout loadedActions = null!;
	private LinearLayout pickActions = null!;
	private readonly List<Button> busyButtons = new();
	private TaskCompletionSource<string?>? webPickCompletion;
	private bool webPickWantsLoad;
	private bool busy;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		AppHost.Start(FilesDir!.AbsolutePath);
		PlatformDialogHandlers.OpenFile = PickFileForWebUi;
		PlatformDialogHandlers.OpenFolder = () => Task.FromResult<string?>(EnsureRippedDirectory());
		PlatformDialogHandlers.SaveFolder = () => Task.FromResult<string?>(EnsureRippedDirectory());

		BuildUi();
		UiLogger.Instance.LineAdded += OnLogLine;
		foreach (string line in UiLogger.Snapshot())
		{
			AppendLog(line);
		}

		RefreshState();
		_ = WarmUpServerAsync();

		if (Intent?.Data is AUri viewUri && Intent.Action == Intent.ActionView)
		{
			_ = ImportAndLoadAsync(new List<AUri> { viewUri });
		}
	}

	protected override void OnDestroy()
	{
		UiLogger.Instance.LineAdded -= OnLogLine;
		base.OnDestroy();
	}

	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);
		if (intent?.Data is AUri uri && intent.Action == Intent.ActionView)
		{
			_ = ImportAndLoadAsync(new List<AUri> { uri });
		}
	}

	// ---------- UI ----------

	private void BuildUi()
	{
		Window!.SetStatusBarColor(Background);
		Window.SetNavigationBarColor(Background);

		root = new FrameLayout(this);
		root.SetBackgroundColor(Background);

		home = new ScrollView(this);
		LinearLayout column = new(this) { Orientation = Orientation.Vertical };
		int pad = Dp(20);
		column.SetPadding(pad, Dp(36), pad, pad);
		home.AddView(column);

		column.AddView(MakeText("AssetRipper", 30, TextPrimary, bold: true));
		column.AddView(MakeText("Extrae, explora y descompila juegos Unity directamente desde tu celular.", 14, TextMuted, topMargin: 4));

		LinearLayout card = MakeCard();
		column.AddView(card, MatchWrap(topMargin: Dp(24)));

		statusText = MakeText("Iniciando motor…", 15, TextPrimary);
		card.AddView(statusText);

		progress = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Indeterminate = true };
		progress.IndeterminateDrawable?.SetColorFilter(Accent, PorterDuff.Mode.SrcIn!);
		progress.Visibility = ViewStates.Gone;
		card.AddView(progress, MatchWrap(topMargin: Dp(10)));

		pickActions = new LinearLayout(this) { Orientation = Orientation.Vertical };
		card.AddView(pickActions, MatchWrap(topMargin: Dp(14)));
		pickActions.AddView(MakeButton("Abrir APK / XAPK / APKS / OBB", primary: true, onClick: PickFilesFromHome));
		pickActions.AddView(MakeText("Puedes elegir varios archivos a la vez (por ejemplo un APK base junto con su OBB).", 12, TextMuted, topMargin: 8));

		loadedActions = new LinearLayout(this) { Orientation = Orientation.Vertical };
		card.AddView(loadedActions, MatchWrap(topMargin: Dp(14)));
		loadedActions.AddView(MakeButton("Explorar assets y scripts", primary: true, onClick: ShowWeb));
		loadedActions.AddView(MakeButton("Exportar proyecto Unity (ZIP en Descargas)", primary: false, onClick: () => _ = ExportAsync(unityProject: true)));
		loadedActions.AddView(MakeButton("Exportar solo contenido (ZIP en Descargas)", primary: false, onClick: () => _ = ExportAsync(unityProject: false)));
		loadedActions.AddView(MakeButton("Cerrar y cargar otro archivo", primary: false, onClick: ResetLoaded));

		column.AddView(MakeText("Registro", 13, TextMuted, topMargin: 22));
		logScroll = new ScrollView(this);
		logScroll.SetBackgroundColor(Surface);
		logText = new TextView(this) { Typeface = Typeface.Monospace };
		logText.SetTextSize(ComplexUnitType.Sp, 11);
		logText.SetTextColor(TextMuted);
		logText.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
		logText.SetTextIsSelectable(true);
		logScroll.AddView(logText);
		column.AddView(logScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(240)) { TopMargin = Dp(6) });

		column.AddView(MakeText("Las exportaciones se guardan como ZIP en Descargas/AssetRipper (en Android 9 o menor: Android/data/com.assetripper.mobile/files/Download). AssetRipper es software libre (GPL-3.0).", 11, TextMuted, topMargin: 14));

		webContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
		webContainer.Visibility = ViewStates.Gone;
		LinearLayout bar = new(this) { Orientation = Orientation.Horizontal };
		bar.SetBackgroundColor(Surface);
		bar.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
		bar.AddView(MakeSmallButton("← Inicio", ShowHome));
		bar.AddView(MakeSmallButton("Atrás", () => { if (webView.CanGoBack()) webView.GoBack(); }));
		bar.AddView(MakeSmallButton("Recargar", () => webView.Reload()));
		webContainer.AddView(bar);

		webView = new WebView(this);
		WebSettings settings = webView.Settings!;
		settings.JavaScriptEnabled = true;
		settings.DomStorageEnabled = true;
		settings.BuiltInZoomControls = true;
		settings.DisplayZoomControls = false;
		settings.UseWideViewPort = true;
		settings.LoadWithOverviewMode = true;
		webView.SetWebViewClient(new AppWebViewClient());
		webView.SetBackgroundColor(Background);
		webContainer.AddView(webView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

		root.AddView(home, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
		root.AddView(webContainer, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
		SetContentView(root);
	}

	private void ShowHome()
	{
		webContainer.Visibility = ViewStates.Gone;
		home.Visibility = ViewStates.Visible;
	}

	private void ShowWeb()
	{
		if (webView.Url is null)
		{
			webView.LoadUrl(AppHost.BaseUrl);
		}
		home.Visibility = ViewStates.Gone;
		webContainer.Visibility = ViewStates.Visible;
	}

	public override void OnBackPressed()
	{
		if (webContainer.Visibility == ViewStates.Visible)
		{
			if (webView.CanGoBack())
			{
				webView.GoBack();
			}
			else
			{
				ShowHome();
			}
			return;
		}
		base.OnBackPressed();
	}

	private void RefreshState()
	{
		RunOnUiThread(() =>
		{
			bool loaded = GameFileLoader.IsLoaded;
			loadedActions.Visibility = loaded && !busy ? ViewStates.Visible : ViewStates.Gone;
			pickActions.Visibility = !loaded && !busy ? ViewStates.Visible : ViewStates.Gone;
			progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
			if (!busy)
			{
				statusText.Text = loaded
					? "Juego cargado. Ya puedes explorarlo o exportarlo."
					: "Listo. Elige un APK de un juego hecho con Unity.";
			}
		});
	}

	private void SetBusy(bool value, string? message = null)
	{
		busy = value;
		RunOnUiThread(() =>
		{
			if (message is not null)
			{
				statusText.Text = message;
			}
		});
		RefreshState();
	}

	private void SetStatus(string message) => RunOnUiThread(() => statusText.Text = message);

	// ---------- Logging ----------

	private void OnLogLine(string line) => RunOnUiThread(() => AppendLog(line));

	private void AppendLog(string line)
	{
		logText.Append(line + "\n");
		if (logText.Text?.Length > 40000)
		{
			logText.Text = logText.Text[^30000..];
		}
		logScroll.Post(() => logScroll.FullScroll(FocusSearchDirection.Down));
	}

	// ---------- File picking / importing ----------

	private void PickFilesFromHome()
	{
		webPickWantsLoad = true;
		webPickCompletion = null;
		LaunchPicker();
	}

	private Task<string?> PickFileForWebUi()
	{
		TaskCompletionSource<string?> completion = new();
		webPickCompletion = completion;
		webPickWantsLoad = false;
		RunOnUiThread(LaunchPicker);
		return completion.Task;
	}

	private void LaunchPicker()
	{
		Intent intent = new(Intent.ActionOpenDocument);
		intent.AddCategory(Intent.CategoryOpenable);
		intent.SetType("*/*");
		intent.PutExtra(Intent.ExtraAllowMultiple, true);
		StartActivityForResult(intent, PickFilesRequest);
	}

	protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		base.OnActivityResult(requestCode, resultCode, data);
		if (requestCode != PickFilesRequest)
		{
			return;
		}

		List<AUri> uris = new();
		if (resultCode == Result.Ok && data is not null)
		{
			if (data.ClipData is { } clip)
			{
				for (int i = 0; i < clip.ItemCount; i++)
				{
					if (clip.GetItemAt(i)?.Uri is { } u)
					{
						uris.Add(u);
					}
				}
			}
			else if (data.Data is { } single)
			{
				uris.Add(single);
			}
		}

		TaskCompletionSource<string?>? completion = webPickCompletion;
		webPickCompletion = null;

		if (uris.Count == 0)
		{
			completion?.TrySetResult(null);
			return;
		}

		if (completion is not null)
		{
			// The web UI asked for a path; copy the first file and hand the path back.
			_ = Task.Run(async () =>
			{
				try
				{
					List<string> paths = await CopyToCacheAsync(uris);
					completion.TrySetResult(paths[0]);
				}
				catch (Exception ex)
				{
					Logger.Error("Could not import the selected file.", ex);
					completion.TrySetResult(null);
				}
			});
		}
		else
		{
			_ = ImportAndLoadAsync(uris);
		}
	}

	private async Task<List<string>> CopyToCacheAsync(List<AUri> uris)
	{
		string dir = Path.Join(CacheDir!.AbsolutePath, "imports");
		try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
		Directory.CreateDirectory(dir);

		List<string> result = new();
		foreach (AUri uri in uris)
		{
			string name = QueryDisplayName(uri) ?? $"import_{result.Count}.apk";
			name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
			string target = Path.Join(dir, name);
			SetStatus($"Copiando {name}…");
			using Stream input = ContentResolver!.OpenInputStream(uri) ?? throw new IOException("Cannot open " + uri);
			using Stream output = File.Create(target);
			await input.CopyToAsync(output, 1 << 20);
			result.Add(target);
			Logger.Info(LogCategory.Import, $"Imported {name} ({new FileInfo(target).Length / 1024 / 1024} MB)");
		}
		return result;
	}

	private string? QueryDisplayName(AUri uri)
	{
		try
		{
			using Android.Database.ICursor? cursor = ContentResolver!.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
			if (cursor is not null && cursor.MoveToFirst())
			{
				return cursor.GetString(0);
			}
		}
		catch (Exception)
		{
		}
		return uri.LastPathSegment;
	}

	private async Task ImportAndLoadAsync(List<AUri> uris)
	{
		if (busy)
		{
			return;
		}
		SetBusy(true, "Preparando archivos…");
		try
		{
			await WaitForServerAsync();
			List<string> paths = await Task.Run(() => CopyToCacheAsync(uris));
			SetStatus("Descomprimiendo y analizando… esto puede tardar varios minutos en juegos grandes.");
			await Task.Run(() => GameFileLoader.LoadAndProcess(paths));
			RunOnUiThread(() => webView.LoadUrl(AppHost.BaseUrl));
			Logger.Info(LogCategory.Import, "Load finished.");
		}
		catch (Exception ex)
		{
			Logger.Error("Loading failed.", ex);
			SetStatus("No se pudo cargar: " + ex.Message);
		}
		finally
		{
			CleanTemporaryFiles(imports: true);
			SetBusy(false);
		}
	}

	// ---------- Export ----------

	private string EnsureRippedDirectory()
	{
		string dir = AppHost.RippedDirectory;
		Directory.CreateDirectory(dir);
		return dir;
	}

	private async Task ExportAsync(bool unityProject)
	{
		if (busy || !GameFileLoader.IsLoaded)
		{
			return;
		}
		SetBusy(true, "Exportando… no cierres la app.");
		try
		{
			string folder = Path.Join(AppHost.RippedDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
			await Task.Run(async () =>
			{
				if (unityProject)
				{
					await GameFileLoader.ExportUnityProject(folder);
				}
				else
				{
					await GameFileLoader.ExportPrimaryContent(folder);
				}
			});
			SetStatus("Comprimiendo en Descargas…");
			string zipName = $"AssetRipper_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
			string where = await Task.Run(() => ZipToDownloads(folder, zipName));
			try { Directory.Delete(folder, true); } catch (IOException) { }
			SetBusy(false, $"Listo: {where}");
			RunOnUiThread(() => Toast.MakeText(this, "Guardado: " + where, ToastLength.Long)!.Show());
		}
		catch (Exception ex)
		{
			Logger.Error("Export failed.", ex);
			SetBusy(false, "La exportación falló: " + ex.Message);
		}
	}

	private string ZipToDownloads(string folder, string zipName)
	{
		Stream output;
		string location;
		if (OperatingSystem.IsAndroidVersionAtLeast(29))
		{
			ContentValues values = new();
			values.Put(MediaStore.IMediaColumns.DisplayName, zipName);
			values.Put(MediaStore.IMediaColumns.MimeType, "application/zip");
			values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads + "/AssetRipper");
			AUri uri = ContentResolver!.Insert(MediaStore.Downloads.ExternalContentUri!, values) ?? throw new IOException("MediaStore refused the file");
			output = ContentResolver.OpenOutputStream(uri) ?? throw new IOException("Cannot write to Downloads");
			location = "Descargas/AssetRipper/" + zipName;
		}
		else
		{
			// Android 7-9: no MediaStore.Downloads, so use the app's own external folder (no permission needed).
			Java.IO.File dir = GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads) ?? FilesDir!;
			string target = Path.Join(dir.AbsolutePath, zipName);
			output = File.Create(target);
			location = target;
		}
		using (output)
		using (ZipArchive zip = new(output, ZipArchiveMode.Create))
		{
			foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				string entryName = Path.GetRelativePath(folder, file).Replace('\\', '/');
				ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
				using Stream entryStream = entry.Open();
				using Stream input = File.OpenRead(file);
				input.CopyTo(entryStream);
			}
		}
		return location;
	}

	private void ResetLoaded()
	{
		GameFileLoader.Reset();
		CleanTemporaryFiles(imports: true);
		RunOnUiThread(() => webView.LoadUrl(AppHost.BaseUrl));
		RefreshState();
	}

	private void CleanTemporaryFiles(bool imports)
	{
		try
		{
			string temp = Path.Join(LocalFileSystem.ExecutingDirectory, "temp");
			if (Directory.Exists(temp)) Directory.Delete(temp, true);
			if (imports)
			{
				string dir = Path.Join(CacheDir!.AbsolutePath, "imports");
				if (Directory.Exists(dir)) Directory.Delete(dir, true);
			}
		}
		catch (Exception)
		{
		}
	}

	private async Task WarmUpServerAsync()
	{
		SetStatus("Iniciando motor…");
		await WaitForServerAsync();
		RefreshState();
	}

	private static async Task WaitForServerAsync()
	{
		if (!await AppHost.WaitUntilReadyAsync(TimeSpan.FromSeconds(60)))
		{
			throw new InvalidOperationException(AppHost.StartupError ?? "El servidor interno no arrancó.");
		}
	}

	// ---------- View helpers ----------

	private int Dp(int value) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, value, Resources!.DisplayMetrics);

	private TextView MakeText(string text, float sizeSp, Color color, bool bold = false, int topMargin = 0)
	{
		TextView view = new(this) { Text = text };
		view.SetTextSize(ComplexUnitType.Sp, sizeSp);
		view.SetTextColor(color);
		if (bold)
		{
			view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
		}
		view.LayoutParameters = MatchWrap(Dp(topMargin));
		return view;
	}

	private LinearLayout MakeCard()
	{
		LinearLayout card = new(this) { Orientation = Orientation.Vertical };
		Android.Graphics.Drawables.GradientDrawable shape = new();
		shape.SetColor(Surface);
		shape.SetCornerRadius(Dp(16));
		card.Background = shape;
		card.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
		return card;
	}

	private Button MakeButton(string text, bool primary, Action onClick)
	{
		Button button = new(this) { Text = text };
		button.SetAllCaps(false);
		button.SetTextColor(primary ? Color.ParseColor("#06210F") : TextPrimary);
		Android.Graphics.Drawables.GradientDrawable shape = new();
		shape.SetColor(primary ? Accent : Color.ParseColor("#2A333D"));
		shape.SetCornerRadius(Dp(12));
		button.Background = shape;
		button.LayoutParameters = MatchWrap(Dp(8));
		button.Click += (_, _) => onClick();
		busyButtons.Add(button);
		return button;
	}

	private Button MakeSmallButton(string text, Action onClick)
	{
		Button button = new(this) { Text = text };
		button.SetAllCaps(false);
		button.SetTextColor(TextPrimary);
		button.SetBackgroundColor(Color.Transparent);
		button.Click += (_, _) => onClick();
		return button;
	}

	private static LinearLayout.LayoutParams MatchWrap(int topMargin = 0)
	{
		return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = topMargin };
	}

	private sealed class AppWebViewClient : WebViewClient
	{
		public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => false;
	}
}
