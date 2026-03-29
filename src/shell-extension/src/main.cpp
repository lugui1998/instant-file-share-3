#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <filesystem>
#include <string>
#include <vector>

namespace
{
    namespace fs = std::filesystem;

    constexpr wchar_t kPipeName[] = LR"(\\.\pipe\InstantFileShare.Agent)";
    constexpr wchar_t kFileVerbKeyPath[] = LR"(Software\Classes\*\shell\InstantFileShare)";
    constexpr wchar_t kFileCommandKeyPath[] = LR"(Software\Classes\*\shell\InstantFileShare\command)";
    constexpr wchar_t kFolderZipVerbKeyPath[] = LR"(Software\Classes\Directory\shell\InstantFileShareFolderZip)";
    constexpr wchar_t kFolderZipCommandKeyPath[] = LR"(Software\Classes\Directory\shell\InstantFileShareFolderZip\command)";
    constexpr wchar_t kFolderZipBackgroundVerbKeyPath[] = LR"(Software\Classes\Directory\Background\shell\InstantFileShareFolderZip)";
    constexpr wchar_t kFolderZipBackgroundCommandKeyPath[] = LR"(Software\Classes\Directory\Background\shell\InstantFileShareFolderZip\command)";
    constexpr wchar_t kFolderBrowseVerbKeyPath[] = LR"(Software\Classes\Directory\shell\InstantFileShareFolderBrowse)";
    constexpr wchar_t kFolderBrowseCommandKeyPath[] = LR"(Software\Classes\Directory\shell\InstantFileShareFolderBrowse\command)";
    constexpr wchar_t kFolderBrowseBackgroundVerbKeyPath[] = LR"(Software\Classes\Directory\Background\shell\InstantFileShareFolderBrowse)";
    constexpr wchar_t kFolderBrowseBackgroundCommandKeyPath[] = LR"(Software\Classes\Directory\Background\shell\InstantFileShareFolderBrowse\command)";

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

    std::wstring BuildPayload(const std::wstring& command, const std::wstring& filePath)
    {
        return L"{\"command\":\"" + EscapeJson(command) + L"\",\"filePath\":\"" + EscapeJson(filePath) + L"\"}\n";
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

    bool FileExists(const fs::path& path)
    {
        std::error_code errorCode;
        return fs::is_regular_file(path, errorCode);
    }

    bool DirectoryExists(const fs::path& path)
    {
        std::error_code errorCode;
        return fs::is_directory(path, errorCode);
    }

    fs::path FindRepositoryRoot(fs::path current)
    {
        current = current.lexically_normal();

        while (!current.empty())
        {
            if (FileExists(current / "InstantFileShare.slnx") || DirectoryExists(current / ".git"))
            {
                return current;
            }

            const auto parent = current.parent_path();
            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        return {};
    }

    std::wstring ResolveAgentExecutablePath()
    {
        const auto helperPath = fs::path(GetExecutablePath());
        const auto helperDirectory = helperPath.parent_path();
        const auto repositoryRoot = FindRepositoryRoot(helperDirectory);

        std::vector<fs::path> candidates;
        candidates.reserve(6);
        candidates.push_back(helperDirectory / "InstantFileShare.Agent.exe");
        candidates.push_back(helperDirectory.parent_path() / "InstantFileShare.Agent.exe");

        if (!repositoryRoot.empty())
        {
            candidates.push_back(repositoryRoot / "src" / "agent" / "InstantFileShare.Agent" / "bin" / "Debug" / "net8.0-windows" / "InstantFileShare.Agent.exe");
            candidates.push_back(repositoryRoot / "src" / "agent" / "InstantFileShare.Agent" / "bin" / "Release" / "net8.0-windows" / "InstantFileShare.Agent.exe");
        }

        for (const auto& candidate : candidates)
        {
            if (FileExists(candidate))
            {
                return candidate.wstring();
            }
        }

        return {};
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

    bool TryConnectToAgentPipe(DWORD timeoutMs, HANDLE& pipe)
    {
        pipe = INVALID_HANDLE_VALUE;
        const auto deadline = GetTickCount64() + timeoutMs;

        while (true)
        {
            pipe = CreateFileW(
                kPipeName,
                GENERIC_READ | GENERIC_WRITE,
                0,
                nullptr,
                OPEN_EXISTING,
                0,
                nullptr);

            if (pipe != INVALID_HANDLE_VALUE)
            {
                return true;
            }

            const auto error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PIPE_BUSY)
            {
                return false;
            }

            const auto now = GetTickCount64();
            if (now >= deadline)
            {
                SetLastError(error);
                return false;
            }

            const auto remaining = deadline - now;
            const auto waitSlice = static_cast<DWORD>(remaining > 250 ? 250 : remaining);
            if (error == ERROR_PIPE_BUSY)
            {
                WaitNamedPipeW(kPipeName, waitSlice);
            }
            else
            {
                Sleep(waitSlice);
            }
        }
    }

    bool TryStartAgent()
    {
        const auto agentPath = ResolveAgentExecutablePath();
        if (agentPath.empty())
        {
            return false;
        }

        auto commandLine = L"\"" + agentPath + L"\"";
        auto workingDirectory = fs::path(agentPath).parent_path().wstring();

        STARTUPINFOW startupInfo{};
        startupInfo.cb = sizeof(startupInfo);

        PROCESS_INFORMATION processInformation{};
        const auto started = CreateProcessW(
            agentPath.c_str(),
            commandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_NEW_PROCESS_GROUP,
            nullptr,
            workingDirectory.c_str(),
            &startupInfo,
            &processInformation);

        if (!started)
        {
            return false;
        }

        CloseHandle(processInformation.hThread);
        CloseHandle(processInformation.hProcess);
        return true;
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

    bool RegisterContextMenu(const wchar_t* verbKeyPath, const wchar_t* commandKeyPath, const std::wstring& label, const std::wstring& commandArgument, const std::wstring& targetToken, std::wstring& errorMessage)
    {
        const auto executablePath = GetExecutablePath();
        const auto command = L"\"" + executablePath + L"\" " + commandArgument + L" \"" + targetToken + L"\"";

        if (!SetRegistryString(HKEY_CURRENT_USER, verbKeyPath, nullptr, label) ||
            !SetRegistryString(HKEY_CURRENT_USER, verbKeyPath, L"MUIVerb", label) ||
            !SetRegistryString(HKEY_CURRENT_USER, verbKeyPath, L"Icon", executablePath) ||
            !SetRegistryString(HKEY_CURRENT_USER, commandKeyPath, nullptr, command))
        {
            errorMessage = L"Failed to register the Explorer context menu entry.";
            return false;
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
        return true;
    }

    bool UnregisterContextMenu(const wchar_t* verbKeyPath, std::wstring& errorMessage)
    {
        const auto result = SHDeleteKeyW(HKEY_CURRENT_USER, verbKeyPath);
        if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND)
        {
            errorMessage = L"Failed to remove the Explorer context menu entry.";
            return false;
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
        return true;
    }

    bool SendCreateShareCommand(const std::wstring& command, const std::wstring& filePath, std::wstring& errorMessage)
    {
        HANDLE pipe = INVALID_HANDLE_VALUE;
        if (!TryConnectToAgentPipe(500, pipe))
        {
            const auto initialError = GetLastError();
            if (initialError == ERROR_FILE_NOT_FOUND)
            {
                if (!TryStartAgent() || !TryConnectToAgentPipe(15000, pipe))
                {
                    errorMessage = L"The Instant File Share agent is not running.";
                    return false;
                }
            }
            else
            {
                errorMessage = L"Could not connect to the Instant File Share agent.";
                return false;
            }
        }

        const auto payload = ToUtf8(BuildPayload(command, filePath));
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

        MessageBoxW(nullptr, L"Usage:\ninstant_file_share_shell --share-file <file-path>\ninstant_file_share_shell --share-folder-zip <folder-path>\ninstant_file_share_shell --share-folder-browse <folder-path>\ninstant_file_share_shell --register-file-context-menu\ninstant_file_share_shell --unregister-file-context-menu\ninstant_file_share_shell --register-folder-zip-context-menu\ninstant_file_share_shell --unregister-folder-zip-context-menu\ninstant_file_share_shell --register-folder-browse-context-menu\ninstant_file_share_shell --unregister-folder-browse-context-menu", L"Instant File Share", MB_OK | MB_ICONINFORMATION);
        return 1;
    }

    const std::wstring command = arguments[1];
    std::wstring filePath = argumentCount >= 3 ? arguments[2] : std::wstring{};
    LocalFree(arguments);

    std::wstring errorMessage;
    if (command == L"--register-file-context-menu")
    {
        if (!RegisterContextMenu(kFileVerbKeyPath, kFileCommandKeyPath, L"Copy Share Link", L"--share-file", L"%1", errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }
        return 0;
    }

    if (command == L"--unregister-file-context-menu")
    {
        if (!UnregisterContextMenu(kFileVerbKeyPath, errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }
        return 0;
    }

    if (command == L"--register-folder-zip-context-menu")
    {
        if (!RegisterContextMenu(kFolderZipVerbKeyPath, kFolderZipCommandKeyPath, L"Share Folder as ZIP", L"--share-folder-zip", L"%1", errorMessage) ||
            !RegisterContextMenu(kFolderZipBackgroundVerbKeyPath, kFolderZipBackgroundCommandKeyPath, L"Share Folder as ZIP", L"--share-folder-zip", L"%V", errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }
        return 0;
    }

    if (command == L"--unregister-folder-zip-context-menu")
    {
        if (!UnregisterContextMenu(kFolderZipVerbKeyPath, errorMessage) ||
            !UnregisterContextMenu(kFolderZipBackgroundVerbKeyPath, errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }
        return 0;
    }

    if (command == L"--register-folder-browse-context-menu")
    {
        if (!RegisterContextMenu(kFolderBrowseVerbKeyPath, kFolderBrowseCommandKeyPath, L"Share Folder for Browsing", L"--share-folder-browse", L"%1", errorMessage) ||
            !RegisterContextMenu(kFolderBrowseBackgroundVerbKeyPath, kFolderBrowseBackgroundCommandKeyPath, L"Share Folder for Browsing", L"--share-folder-browse", L"%V", errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }
        return 0;
    }

    if (command == L"--unregister-folder-browse-context-menu")
    {
        if (!UnregisterContextMenu(kFolderBrowseVerbKeyPath, errorMessage) ||
            !UnregisterContextMenu(kFolderBrowseBackgroundVerbKeyPath, errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }
        return 0;
    }

    if (command == L"--share-file" || command == L"--share-folder-zip" || command == L"--share-folder-browse")
    {
        const auto pipeCommand = command == L"--share-folder-zip"
            ? L"share-folder-zip"
            : command == L"--share-folder-browse"
                ? L"share-folder-browse"
                : L"share";

        if (filePath.empty() || !SendCreateShareCommand(pipeCommand, filePath, errorMessage))
        {
            MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
            return 1;
        }

        return 0;
    }

    MessageBoxW(nullptr, L"Unsupported command.", L"Instant File Share", MB_OK | MB_ICONERROR);
    return 1;
}
