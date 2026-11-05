using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TokenTracker.Core.Otlp;

// The Copilot pipeline publishes externally registered tool names as
// SHA-256(name) and keeps no plaintext copy, so a tooltip or table would
// otherwise show 64 hex characters. Because the hash is derived rather than
// assigned, the readable name is recoverable by hashing the public list of
// built-in tool names; MCP server hashes are deliberately left opaque.
public static class ToolNameResolver
{
    private static readonly Lazy<Dictionary<string, string>> Hashed =
        new(BuildIndex);

    private static readonly ConcurrentDictionary<string, string?> Cache =
        new(StringComparer.Ordinal);

    private const string KnownToolNames = """
        addComment
        add_artifact_or_reference
        ask_user
        clickElement
        click_element
        copilot_getNotebookSummary
        create
        createAndRunTask
        create_and_run_task
        create_directory
        create_file
        create_new_jupyter_notebook
        create_new_workspace
        create_session
        debugJavaApplication
        debug_java_application
        deleteComments
        delete_session
        dragElement
        drag_element
        edit
        edit_notebook_file
        extensions_reload
        fetch_webpage
        file_search
        getDebugSessionInfo
        getTaskOutput
        get_current_session
        get_debug_session_info
        get_errors
        get_session_context
        get_task_output
        get_terminal_output
        get_vscode_api
        github_repo
        github_text_search
        glob
        grep
        grep_search
        handleDialog
        handle_dialog
        hoverElement
        hover_element
        invoke_canvas_action
        isolate_session
        kill_terminal
        listComments
        list_agents
        list_artifacts_and_references
        list_canvas_capabilities
        list_dir
        list_powershell
        list_sessions
        manage_todo_list
        memory
        multi_replace_string_in_file
        navigatePage
        navigate_page
        openBrowserPage
        open_browser_page
        open_canvas
        powershell
        problems
        readPage
        read_agent
        read_file
        read_notebook_cell_output
        read_page
        read_powershell
        removeJavaBreakpoints
        remove_artifact_or_reference
        remove_java_breakpoints
        replace_string_in_file
        replyToComment
        resolveComments
        resolve_memory_file_uri
        runPlaywrightCode
        runSubagent
        runTask
        runTests
        run_in_terminal
        run_notebook_cell
        run_playwright_code
        run_task
        screenshotPage
        screenshot_page
        semantic_search
        send_message
        send_to_terminal
        session_store_sql
        setJavaBreakpoint
        set_java_breakpoint
        set_workspace
        skill
        sql
        stop_powershell
        store_memory
        task
        terminal_last_command
        terminal_selection
        testFailure
        typeInPage
        type_in_page
        usages
        view
        viewUnreviewedComments
        view_image
        vscode_askQuestions
        vscode_listCodeUsages
        vscode_renameSymbol
        vscode_searchExtensions_internal
        web_fetch
        write_agent
        """;

    public static string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var candidate = name.Trim();
        if (candidate.Length != 64)
        {
            return null;
        }

        foreach (var character in candidate)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return null;
            }
        }

        candidate = candidate.ToLowerInvariant();
        return Cache.GetOrAdd(
            candidate,
            key => Hashed.Value.TryGetValue(key, out var resolved) ? resolved : null);
    }

    // Tables and tooltips show this instead of the raw value.
    public static string Display(string? name) =>
        Resolve(name) ?? (string.IsNullOrWhiteSpace(name) ? "-" : name);

    private static Dictionary<string, string> BuildIndex()
    {
        var names = KnownToolNames.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = new Dictionary<string, string>(names.Length, StringComparer.Ordinal);
        foreach (var name in names)
        {
            var hash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(name)))
                .ToLowerInvariant();
            index[hash] = name;
        }

        return index;
    }
}
