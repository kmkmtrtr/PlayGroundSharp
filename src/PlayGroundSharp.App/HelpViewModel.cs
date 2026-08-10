using CommunityToolkit.Mvvm.ComponentModel;

namespace PlayGroundSharp.App;

/// <summary>Represents one section in an in-app help topic.</summary>
public sealed record HelpSection(string Heading, string Body, string? Code = null);

/// <summary>Represents one navigable in-app help topic.</summary>
public sealed record HelpTopic(string Title, string Subtitle, IReadOnlyList<HelpSection> Sections);

/// <summary>Provides localized help content without coupling it to the main view model.</summary>
public sealed partial class HelpViewModel : ObservableObject
{
    public HelpViewModel(AppLanguageMode languageMode)
    {
        Topics = languageMode == AppLanguageMode.Japanese ? CreateJapaneseTopics() : CreateEnglishTopics();
        selectedTopic = Topics[0];
    }

    public IReadOnlyList<HelpTopic> Topics { get; }
    [ObservableProperty] private HelpTopic selectedTopic;

    private static IReadOnlyList<HelpTopic> CreateJapaneseTopics() =>
    [
        new("はじめに", "状態を保持しながらC#を一行ずつ試せます。",
        [
            new("基本", "下端の > 行へ式を入力して実行します。変数、メソッド、型、usingは次の入力でも利用できます。", "var values = Enumerable.Range(1, 10).ToArray();\nvalues.Where(x => x % 2 == 0)"),
            new("非同期", "トップレベルawaitをそのまま利用できます。", "await Task.FromResult(42)"),
            new("前回結果", "Lastは直前の結果、Out[index]は指定Submissionの元オブジェクトを参照します。", "Last\nOut[1]")
        ]),
        new("入力と補完", "C#補完、シグネチャ、診断を現在のセッション状態から生成します。",
        [
            new("キー操作", "実行キーがEnterならShift+Enterで改行し、Ctrl+Enter設定ならEnterまたはShift+Enterで改行します。Ctrl+Spaceで補完を開き、Enter／Tab／クリックで確定、↑↓で1件、PageUp／PageDownで1ページ移動、ホイールでスクロールし、Escで閉じます。入力中のメソッドや引数へマウスを置くと署名・Summary・引数説明を表示し、Ctrl+K, Ctrl+Iでもキャレット位置のクイック情報を表示できます。横スクロール領域はホイール、縦にも移動できる領域はShift+ホイールで横移動します。F8／Shift+F8で次／前の診断、F6／Shift+F6でペイン移動ができます。Ctrl+Lで入力欄、Ctrl+Fでシンボル検索へ移動できます。"),
            new("履歴", "一行入力で上下キーを押すと履歴を移動します。過去の入力行をクリックして現在の入力へコピーできます。"),
            new("ファイルとフォルダのドロップ", "入力欄へドロップすると操作メニューを表示します。パス挿入、JSONやテキストの読み込みコード、フォルダ内ファイルの列挙を選べます。.pgsworkspaceは確認後にワークスペースとして開けます。コードを自動実行することはありません。複数パスは配列として挿入します。"),
            new("補完の確定", "候補は入力内容に応じて絞り込まれます。未usingの型や拡張メソッドには「using 名前空間」を表示し、確定時にusingを自動追加します。拡張メソッドを手入力した場合も、名前空間が一意なら実行前に追加します。候補が表示されている間はEnter、Tab、または候補のクリックで挿入します。")
        ]),
        new("シンボル", "名前空間、型、メソッドとドキュメントを探索します。",
        [
            new("ホバーと詳細表示", "項目へマウスを置くと署名と概要をすばやく確認できます。enumは展開すると各メンバーと定数値を表示します。クリックするとパラメーターや戻り値を右側のフライアウトへ表示します。Explorer右端をドラッグすると一覧幅を変更できます。深い階層は横スクロールホイール、またはShift+ホイールで横移動できます。"),
            new("日本語ドキュメント", "アセンブリ付属のXMLコメントは通常英語です。内容を不正確に自動翻訳せず、.NET APIではホバーのクイック情報やシンボル エクスプローラーからMicrosoft Learnの日本語ページを開けます。"),
            new("検索", "名前空間、型、メソッド、コメント、アセンブリ名を横断検索します。Ctrl+Fで検索欄へ移動し、Enterまたは↓で先頭の一致項目へ移動します。")
        ]),
        new("ワークスペース", "セッションを保存し、後から再構築できます。",
        [
            new("保存内容", "Submission、入力途中のテキスト、using、DLL参照、NuGetパッケージの正確なバージョンを保存します。実行中オブジェクトそのものは保存しません。"),
            new("読み込み方式", "新しいWorkerを起動し、依存関係を復元してからSubmissionを順番に再実行します。このためファイル書き込みなどの副作用も再実行されます。"),
            new("利用方法", "ヘッダーの［ファイル］から.pgsworkspaceを保存または開きます。入力欄へ.pgsworkspaceをドロップして開くこともできます。欠落したローカルDLLは警告してスキップします。")
        ]),
        new("大容量データ", "巨大なファイル全体を不用意にメモリへ載せないためのDataヘルパーです。",
        [
            new("確認とプレビュー", "Inspectは内容を読まずサイズを返します。PreviewTextとReadBytesは最大1 MiBに制限されます。", "Data.Inspect(@\"C:\\data\\large.json\")\nData.PreviewText(@\"C:\\data\\large.log\", 65536)"),
            new("行ストリーム", "ReadLinesは遅延列挙です。Takeなどで件数を絞ってください。", "Data.ReadLines(@\"C:\\data\\large.csv\").Take(100)"),
            new("JSON", "単一のJSON値をJsonNodeで返します。通常はReadJsonAsyncを使い、大きなトップレベル配列を先頭N件だけ確認するときはReadJsonArrayAsyncを使います。ファイルのドロップ時は拡張子に依存せず、JSON、JSONL、テキスト、行ストリーム、バイナリの読み方を選べます。", "var json = await Data.ReadJsonAsync(@\"C:\\data\\settings.json\", ExecutionCancellation);\nvar rows = await Data.ReadJsonArrayAsync(@\"C:\\data\\large-array.json\", 1000, ExecutionCancellation);"),
            new("JSON Lines", "ReadAllJsonLinesAsyncはJSONL全体をリストとして読みます。全件をメモリへ載せたくない場合はStreamJsonLinesAsyncで逐次処理してください。件数を絞る場合はReadJsonLinesAsyncを使えます。", "var rows = await Data.ReadJsonLinesAsync(@\"C:\\data\\events.jsonl\", 1000, ExecutionCancellation);\nvar allRows = await Data.ReadAllJsonLinesAsync(@\"C:\\data\\events.jsonl\", ExecutionCancellation);\n\nawait foreach (var row in Data.StreamJsonLinesAsync(@\"C:\\data\\events.jsonl\", ExecutionCancellation))\n{\n    // 1件ずつ処理\n}")
        ]),
        new("依存関係", "NuGet、DLL、usingを実行環境と補完環境へ反映します。",
        [
            new("NuGet", "右上の［セッション］→［NuGet］から検索し、候補からバージョンを選択するか正確なバージョンを直接入力して追加できます。コロンコマンドも利用できます。", ":package add Humanizer.Core --version 3.0.10\n:package list"),
            new("DLLとusing", "［セッション］→［ライブラリ］の［DLLを追加］から複数選択できます。入力欄へDLLをドロップして［DLL参照として追加］を選ぶか、コロンコマンドも利用できます。", ":reference add \"C:\\Libraries\\Example.dll\"\n:using add Example.Namespace")
        ]),
        new("停止と安全性", "Worker分離は安定性のためであり、サンドボックスではありません。",
        [
            new("停止", "Escまたは停止ボタンで、実行前の入力解析、実行中のコード、NuGetの検索・追加を中断できます。実行前なら未実行の入力を復元します。待機や長いループにExecutionCancellationを渡すと、セッション状態を保ったまま協調停止できます。応答しないコードはWorkerを強制終了するため、Worker内の変数状態が失われます。", "await Task.Delay(10_000, ExecutionCancellation)\nExecutionCancellation.ThrowIfCancellationRequested()"),
            new("権限", "任意のC#コードとパッケージは現在のWindowsユーザー権限で動作します。信頼できないコード、DLL、パッケージを実行しないでください。")
        ])
    ];

    private static IReadOnlyList<HelpTopic> CreateEnglishTopics() =>
    [
        new("Getting started", "Evaluate C# incrementally while preserving session state.",
        [
            new("Basics", "Enter code on the > line. Variables, methods, types, and usings remain available.", "var values = Enumerable.Range(1, 10).ToArray();\nvalues.Sum()"),
            new("Async and results", "Top-level await is supported. Last and Out[index] retain original result objects.", "await Task.FromResult(42)\nLast\nOut[1]")
        ]),
        new("Input and IntelliSense", "Completion and diagnostics use the current session state.",
        [
            new("Keys", "With Enter-to-run, Shift+Enter inserts a line break; with Ctrl+Enter-to-run, Enter or Shift+Enter inserts one. Ctrl+Space opens completion; Enter, Tab, or a click accepts; arrow keys move one item; PageUp/PageDown move one page; the wheel scrolls; and Esc closes it. Hover a method or argument for its signature, summary, and parameter documentation; Ctrl+K, Ctrl+I also shows Quick Info at the caret. The wheel moves horizontal-only regions; use Shift+wheel when a region can also scroll vertically. F8/Shift+F8 moves through diagnostics; F6/Shift+F6 cycles panes. Ctrl+L focuses input and Ctrl+F focuses symbol search."),
            new("Automatic imports", "Unimported types and extension methods show a 'using Namespace' badge. Accepting one adds that using before inserting the completion."),
            new("Dropping files and folders", "Drop onto the input editor to choose between inserting a path, generating data-reading code, or enumerating a folder. A .pgsworkspace file can be opened as a workspace after confirmation. Nothing is executed automatically. Multiple paths are inserted as an array."),
            new("History", "Use Up and Down on a single line, or click a prior input to copy it into the editor.")
        ]),
        new("Symbol explorer", "Browse namespaces, types, methods, and XML documentation.",
        [
            new("Hover and details", "Hover for a compact signature and summary, then move into the popup to open Microsoft Learn for .NET APIs. Expand an enum to inspect every member and its constant value. Click for documentation in a right-side flyout. Ctrl+F focuses search; Enter or Down moves to the first match. Drag the Explorer edge to resize it. Use a horizontal wheel or Shift+wheel to move through deep hierarchies."),
            new("Localized docs", "Assembly XML documentation is commonly English. Framework symbols link to the localized Microsoft Learn API page rather than applying an unreliable automatic translation.")
        ]),
        new("Workspaces", "Save and reconstruct a session later.",
        [
            new("Contents", "Submissions, draft input, usings, DLL references, and exact package versions are saved. Live objects are not serialized."),
            new("Replay", "Open a .pgsworkspace from File or drop it onto the input editor. A fresh Worker restores dependencies and replays submissions, so side effects such as file writes run again.")
        ]),
        new("Large files and JSON", "Data helpers avoid loading entire files by default.",
        [
            new("Preview", "PreviewText and ReadBytes are bounded to 1 MiB.", "Data.Inspect(@\"C:\\data\\large.json\")\nData.PreviewText(@\"C:\\data\\large.log\")"),
            new("JSON", "ReadJsonAsync returns one JSON value as JsonNode. Use ReadJsonArrayAsync to inspect only the first N values of a large top-level array. File drop lets you choose JSON, JSONL, complete text, a line stream, or binary independently of the extension.", "var json = await Data.ReadJsonAsync(@\"C:\\data\\settings.json\", ExecutionCancellation);\nvar rows = await Data.ReadJsonArrayAsync(@\"C:\\data\\large-array.json\", 1000, ExecutionCancellation);"),
            new("JSON Lines", "ReadAllJsonLinesAsync materializes the complete JSONL file. Use StreamJsonLinesAsync to process all values without retaining them in memory, or ReadJsonLinesAsync for a bounded list.", "var rows = await Data.ReadJsonLinesAsync(@\"C:\\data\\events.jsonl\", 1000, ExecutionCancellation);\nvar allRows = await Data.ReadAllJsonLinesAsync(@\"C:\\data\\events.jsonl\", ExecutionCancellation);\n\nawait foreach (var row in Data.StreamJsonLinesAsync(@\"C:\\data\\events.jsonl\", ExecutionCancellation))\n{\n    // Process one value\n}")
        ]),
        new("Dependencies", "Add NuGet packages, DLLs, and usings to execution and IntelliSense.",
        [
            new("Commands", "Search in Workspace > NuGet, then choose a version or enter an exact version before installing. Add one or more local assemblies in Libraries, drop a DLL onto the input, or use colon commands.", ":package add Humanizer.Core --version 3.0.10\n:reference add \"C:\\Libraries\\Example.dll\"\n:using add Example.Namespace")
        ]),
        new("Cancellation and security", "Worker isolation improves recovery; it is not a sandbox.",
        [
            new("Stop", "Press Esc or Stop to cancel pre-execution analysis, running code, NuGet searches, and package installation. Cancelling analysis restores the unsubmitted input. Pass ExecutionCancellation to waits and long loops to stop cooperatively without losing session state. A non-responsive Worker is terminated, so Worker variables are lost.", "await Task.Delay(10_000, ExecutionCancellation)\nExecutionCancellation.ThrowIfCancellationRequested()"),
            new("Permissions", "Submitted code and packages run with your current Windows user permissions. Never run untrusted code, DLLs, or packages.")
        ])
    ];
}
