using System.Text.Json;
using InstantFileShare.Core;

namespace InstantFileShare.Data;

internal static class AppSettingsJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(AppSettings settings)
    {
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    public static AppSettings DeserializeOrDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppSettings();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var settings = document.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
            if (ShouldInheritReceiveDiagnostics(document.RootElement))
            {
                settings = settings with { ReceiveTransferDiagnosticsEnabled = true };
            }

            if (TryGetBrowserTransferEncryptionPolicy(document.RootElement, out var legacyEncryptionPolicy))
            {
                if (!HasProperty(document.RootElement, "browserDownloadEncryptionPolicy") &&
                    !HasProperty(document.RootElement, nameof(AppSettings.BrowserDownloadEncryptionPolicy)))
                {
                    settings = settings with { BrowserDownloadEncryptionPolicy = legacyEncryptionPolicy };
                }

                if (!HasProperty(document.RootElement, "receiveUploadEncryptionPolicy") &&
                    !HasProperty(document.RootElement, nameof(AppSettings.ReceiveUploadEncryptionPolicy)))
                {
                    settings = settings with { ReceiveUploadEncryptionPolicy = legacyEncryptionPolicy };
                }
            }

            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    private static bool ShouldInheritReceiveDiagnostics(JsonElement root)
    {
        if (HasProperty(root, "receiveTransferDiagnosticsEnabled") ||
            HasProperty(root, nameof(AppSettings.ReceiveTransferDiagnosticsEnabled)))
        {
            return false;
        }

        return TryGetBoolean(root, "browserTransferDiagnosticsEnabled", out var browserDiagnosticsEnabled) && browserDiagnosticsEnabled ||
            TryGetBoolean(root, nameof(AppSettings.BrowserTransferDiagnosticsEnabled), out browserDiagnosticsEnabled) && browserDiagnosticsEnabled;
    }

    private static bool HasProperty(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out _);
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetBrowserTransferEncryptionPolicy(JsonElement root, out BrowserTransferEncryptionPolicy value)
    {
        value = BrowserTransferEncryptionPolicy.HttpOnly;
        return TryGetBrowserTransferEncryptionPolicy(root, "browserTransferEncryptionPolicy", out value) ||
            TryGetBrowserTransferEncryptionPolicy(root, "BrowserTransferEncryptionPolicy", out value);
    }

    private static bool TryGetBrowserTransferEncryptionPolicy(JsonElement root, string propertyName, out BrowserTransferEncryptionPolicy value)
    {
        value = BrowserTransferEncryptionPolicy.HttpOnly;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return Enum.TryParse(property.GetString(), ignoreCase: true, out value) &&
                Enum.IsDefined(value);
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var intValue) &&
            Enum.IsDefined(typeof(BrowserTransferEncryptionPolicy), intValue))
        {
            value = (BrowserTransferEncryptionPolicy)intValue;
            return true;
        }

        return false;
    }
}
