using Mellow.SlopFactory.App.Services;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using Microsoft.Extensions.Logging;

namespace Mellow.SlopFactory.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		builder.Services.AddSingleton(Preferences.Default);
		builder.Services.AddSingleton(SecureStorage.Default);
		builder.Services.AddSingleton<SettingsService>();
		builder.Services.AddSingleton<ImageGenerationService>();
		builder.Services.AddSingleton<LibraryService>();
		builder.Services.AddScoped<ThumbnailService>();
		builder.Services.AddScoped<CreateSession>();
		builder.Services.AddSingleton(FileSaver.Default);
		builder.Services.AddSingleton(FolderPicker.Default);
		builder.Services.AddSingleton<ExportService>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
