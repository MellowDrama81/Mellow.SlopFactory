using Mellow.SlopFactory.Core;

namespace Mellow.SlopFactory.App.Services;

/// <summary>Library folders the Create page files images into. Null means the top of the library.</summary>
/// <param name="UploadedFolderId">For images added as inputs on the Create page.</param>
/// <param name="GeneratedFolderId">For generated images.</param>
public record LibrarySettings(string? UploadedFolderId, string? GeneratedFolderId);

/// <summary>
/// Persists app settings on the device. The Comfy base URL and library folders go in Preferences; the API key goes
/// in SecureStorage so it is protected by the platform's credential store.
/// </summary>
public class SettingsService
{
	private const string ComfyBaseUrlKey = "comfy_base_url";
	private const string ComfyApiKeyKey = "comfy_api_key";
	private const string UploadedFolderKey = "library_uploaded_folder";
	private const string GeneratedFolderKey = "library_generated_folder";

	private readonly IPreferences preferences;
	private readonly ISecureStorage secureStorage;

	public SettingsService(IPreferences preferences, ISecureStorage secureStorage)
	{
		this.preferences = preferences;
		this.secureStorage = secureStorage;
	}

	public async Task<ComfyApiSettings> LoadComfyAsync()
	{
		var baseUrl = preferences.Get(ComfyBaseUrlKey, string.Empty);

		return new ComfyApiSettings
		{
			BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl,
			ApiKey = await secureStorage.GetAsync(ComfyApiKeyKey),
		};
	}

	public async Task SaveComfyAsync(ComfyApiSettings settings)
	{
		if (string.IsNullOrWhiteSpace(settings.BaseUrl))
		{
			preferences.Remove(ComfyBaseUrlKey);
		}
		else
		{
			preferences.Set(ComfyBaseUrlKey, settings.BaseUrl.Trim());
		}

		if (string.IsNullOrWhiteSpace(settings.ApiKey))
		{
			secureStorage.Remove(ComfyApiKeyKey);
		}
		else
		{
			await secureStorage.SetAsync(ComfyApiKeyKey, settings.ApiKey.Trim());
		}
	}

	public LibrarySettings LoadLibrary()
	{
		return new LibrarySettings(GetOrNull(UploadedFolderKey), GetOrNull(GeneratedFolderKey));
	}

	public void SaveLibrary(LibrarySettings settings)
	{
		SetOrRemove(UploadedFolderKey, settings.UploadedFolderId);
		SetOrRemove(GeneratedFolderKey, settings.GeneratedFolderId);
	}

	private string? GetOrNull(string key)
	{
		var value = preferences.Get(key, string.Empty);
		return string.IsNullOrEmpty(value) ? null : value;
	}

	private void SetOrRemove(string key, string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			preferences.Remove(key);
		}
		else
		{
			preferences.Set(key, value);
		}
	}
}
