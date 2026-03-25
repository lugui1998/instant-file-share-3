#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <string>
#include <vector>

namespace
{
    constexpr wchar_t kPipeName[] = LR"(\\.\pipe\InstantFileShare.Agent)";
    constexpr wchar_t kVerbKeyPath[] = LR"(Software\Classes\*\shell\InstantFileShare)";
    constexpr wchar_t kCommandKeyPath[] = LR"(Software\Classes\*\shell\InstantFileShare\command)";

    std::wstring EscapeJson(const std::wstring& value)
    {
        std::wstring output;
        output.reserve(value.size() + 16);

        for (const auto character : value)
        {
            switch (character)
            {
            case L'\\':
                output += LR"(\\)";
                break;
            case L'"':
                output += LR"(\")";
                break;
            case L'\r':
                output += LR"(\r)";
                break;
            case L'\n':
                output += LR"(\n)";
                break;
            case L'\t':
                output += LR"(\t)";
                break;
            default:
                output += character;
                break;
            }
        }

        return output;
    }

    std::wstring BuildPayload(const std::wstring& filePath)
    {
        return L"{\"command\":\"share\",\"filePath\":\"" + EscapeJson(filePath) + L"\"}\n";
    }

    std::string ToUtf8(const std::wstring& value)
    {
        if (value.empty())
        {
            return {};
        }

        const auto size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        std::string output(size, '\0');
        WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), output.data(), size, nullptr, nullptr);
        return output;
    }

    std::wstring GetExecutablePath()
    {
        std::wstring buffer(MAX_PATH, L'\0');
        const auto length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        buffer.resize(length);
        return buffer;
    }

    std::wstring GetFirstArgument()
    {
        int argumentCount = 0;
        LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
        if (arguments == nullptr || argumentCount < 2)
        {
            if (arguments != nullptr)
            {
                LocalFree(arguments);
            }

            return {};
        }

        std::wstring path = arguments[1];
        LocalFree(arguments);
        return path;
    }

    bool ContainsSuccessFlag(const std::string& response)
    {
        return response.find("\"Success\":true") != std::string::npos
            || response.find("\"success\":true") != std::string::npos;
    }

    std::wstring ExtractMessage(const std::string& response)
    {
        const auto messageKey = response.find("\"Message\":\"");
        const auto lowercaseKey = response.find("\"message\":\"");
        const auto keyPosition = messageKey != std::string::npos ? messageKey : lowercaseKey;
        if (keyPosition == std::string::npos)
        {
            return {};
        }

        const auto valueStart = response.find('"', keyPosition + 10);
        if (valueStart == std::string::npos)
        {
            return {};
        }

        std::string value;
        value.reserve(128);

        for (auto index = valueStart + 1; index < response.size(); ++index)
        {
            const auto character = response[index];
            if (character == '\\' && index + 1 < response.size())
            {
                const auto escaped = response[++index];
                switch (escaped)
                {
                case '\\':
                case '"':
                case '/':
                    value.push_back(escaped);
                    break;
                case 'n':
                    value.push_back('\n');
                    break;
                case 'r':
                    value.push_back('\r');
                    break;
                case 't':
                    value.push_back('\t');
                    break;
                default:
                    value.push_back(escaped);
                    break;
                }

                continue;
            }

            if (character == '"')
            {
                break;
            }

            value.push_back(character);
        }

        if (value.empty())
        {
            return {};
        }

        const auto length = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
        std::wstring output(length, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), output.data(), length);
        return output;
    }

    bool SetRegistryString(HKEY root, const wchar_t* subKey, const wchar_t* valueName, const std::wstring& value)
    {
        HKEY key = nullptr;
        if (RegCreateKeyExW(root, subKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS)
        {
            return false;
        }

        const auto result = RegSetValueExW(
            key,
            valueName,
            0,
            REG_SZ,
            reinterpret_cast<const BYTE*>(value.c_str()),
            static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));

        RegCloseKey(key);
        return result == ERROR_SUCCESS;
    }

    bool RegisterContextMenu(std::wstring& errorMessage)
    {
        const auto executablePath = GetExecutablePath();
        const auto command = L"\"" + executablePath + L"\" \"%1\"";

        if (!SetRegistryString(HKEY_CURRENT_USER, kVerbKeyPath, nullptr, L"Copy Share Link") ||
            !SetRegistryString(HKEY_CURRENT_USER, kVerbKeyPath, L"MUIVerb", L"Copy Share Link") ||
            !SetRegistryString(HKEY_CURRENT_USER, kVerbKeyPath, L"Icon", executablePath) ||
            !SetRegistryString(HKEY_CURRENT_USER, kCommandKeyPath, nullptr, command))
        {
            errorMessage = L"Failed to register the Explorer context menu entry.";
            return false;
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
        return true;
    }

    bool UnregisterContextMenu(std::wstring& errorMessage)
    {
        const auto result = SHDeleteKeyW(HKEY_CURRENT_USER, kVerbKeyPath);
        if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND)
        {
            errorMessage = L"Failed to remove the Explorer context menu entry.";
            return false;
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
        return true;
    }

    bool SendCreateShareCommand(const std::wstring& filePath, std::wstring& errorMessage)
    {
        if (!WaitNamedPipeW(kPipeName, 5000))
        {
            errorMessage = L"The Instant File Share agent is not running.";
            return false;
        }

        HANDLE pipe = CreateFileW(
            kPipeName,
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            0,
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE)
        {
            errorMessage = L"Could not connect to the Instant File Share agent.";
            return false;
        }

        const auto payload = ToUtf8(BuildPayload(filePath));
        DWORD bytesWritten = 0;
        if (!WriteFile(pipe, payload.data(), static_cast<DWORD>(payload.size()), &bytesWritten, nullptr))
        {
            CloseHandle(pipe);
            errorMessage = L"Failed to send the share request to the agent.";
            return false;
        }

        std::string response;
        char buffer[512];
        DWORD bytesRead = 0;
        while (ReadFile(pipe, buffer, sizeof(buffer), &bytesRead, nullptr) && bytesRead > 0)
        {
            response.append(buffer, buffer + bytesRead);
            if (response.find('\n') != std::string::npos)
            {
                break;
            }
        }

        CloseHandle(pipe);

        if (!ContainsSuccessFlag(response))
        {
            errorMessage = ExtractMessage(response);
            if (errorMessage.empty())
            {
                errorMessage = L"The agent rejected the share request.";
            }
            return false;
        }

        return true;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int argumentCount = 0;
    LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    if (arguments == nullptr || argumentCount < 2)
    {
        if (arguments != nullptr)
        {
            LocalFree(arguments);
        }

        MessageBoxW(nullptr, L"Usage:\ninstant_file_share_shell <file-path>\ninstant_file_share_shell --register-context-menu\ninstant_file_share_shell --unregister-context-menu", L"Instant File Share", MB_OK | MB_ICONINFORMATION);
        return 1;
    }

    const std::wstring command = arguments[1];
    std::wstring filePath = command;
    LocalFree(arguments);

    std::wstring errorMessage;
    if (command == L"--register-context-menu")
    {
        if (!RegisterContextMenu(errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }
        return 0;
    }

    if (command == L"--unregister-context-menu")
    {
        if (!UnregisterContextMenu(errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }
        return 0;
    }

    if (filePath.empty() || !SendCreateShareCommand(filePath, errorMessage))
    {
        MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
        return 1;
    }

    return 0;
}
