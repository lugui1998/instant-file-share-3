#include <windows.h>
#include <string>
#include <vector>

namespace
{
    constexpr wchar_t kPipeName[] = LR"(\\.\pipe\InstantFileShare.Agent)";

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
            errorMessage = L"The agent rejected the share request.";
            return false;
        }

        return true;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    const std::wstring filePath = GetFirstArgument();
    if (filePath.empty())
    {
        MessageBoxW(nullptr, L"Usage: instant_file_share_shell <file-path>", L"Instant File Share", MB_OK | MB_ICONINFORMATION);
        return 1;
    }

    std::wstring errorMessage;
    if (!SendCreateShareCommand(filePath, errorMessage))
    {
        MessageBoxW(nullptr, errorMessage.c_str(), L"Instant File Share", MB_OK | MB_ICONERROR);
        return 1;
    }

    return 0;
}
