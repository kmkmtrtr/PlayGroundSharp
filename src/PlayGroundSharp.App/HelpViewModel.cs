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
        new("はじめに", "コードを実行し、結果を再利用するまでの基本です。",
        [
            new("実行する", "画面下部の入力欄へC#を書き、設定されている実行キーを押します。宣言した変数、メソッド、型、usingは同じコンソールの次の入力でも利用できます。", "var values = Enumerable.Range(1, 10).ToArray();\nvalues.Where(x => x % 2 == 0)"),
            new("結果を使う", "結果は入力の上に追加されます。Lastは直前の結果、Out[index]は指定した実行結果の元オブジェクトです。トップレベルawaitもそのまま利用できます。", "await Task.FromResult(42)\nLast\nOut[1]"),
            new("詳しく見る", "結果行をダブルクリックするかEnterを押すと［結果の詳細］が開きます。オブジェクトや配列はツリーまたはテーブルで確認できます。")
        ]),
        new("コンソールと.NET", "用途ごとに独立した実行環境を使い分けられます。",
        [
            new("コンソールタブ", "［＋］またはCtrl+Tでコンソールを追加します。各タブはWorker、変数、履歴、参照、using、使用する.NETを個別に保持します。Ctrl+Tab／Ctrl+Shift+Tabで移動し、Ctrl+W、×、または中クリックで閉じます。"),
            new("使用する.NET", "［ワークスペース］の［設定］で、インストール済み.NET SDKから補完・診断・実行に使う.NETを選べます。変更すると現在の変数、型、メソッドは消えますが、結果表示、履歴、参照、usingは残ります。")
        ]),
        new("入力と補完", "C#補完、シグネチャ、診断を現在のセッション状態から生成します。",
        [
            new("補完と説明", "ピリオドの入力またはCtrl+Spaceで候補を開き、Enter、Tab、またはクリックで確定します。未usingの型や拡張メソッドを選ぶと、必要なusingも追加します。シンボルへマウスを置くと署名と説明が表示され、.NET APIはポップアップ内のリンクからMicrosoft Learnを開けます。"),
            new("診断とキー操作", "入力中のエラーと警告はその場で表示されます。F8／Shift+F8で次／前の診断、F6／Shift+F6でペイン移動、Ctrl+Lで入力欄、Ctrl+Fでシンボル検索へ移動できます。Escは補完を閉じ、実行中は停止に使います。Ctrl+Cは選択内容をコピーし、コピー対象がない実行中は停止します。"),
            new("改行と履歴", "実行キーがEnterならShift+Enterで改行し、Ctrl+Enter設定ならEnterまたはShift+Enterで改行します。一行入力で↑↓を押すと履歴を移動でき、過去の入力行をクリックすると入力欄へ戻せます。"),
            new("ファイルをドロップ", "入力欄へファイルやフォルダをドロップすると操作を選べます。パスの挿入、内容を読む式の作成、フォルダ内ファイルの列挙などがあり、自動では実行されません。複数ファイルはまとめて扱えます。")
        ]),
        new("結果と変数", "実行結果を調べ、後から名前を付けて再利用できます。",
        [
            new("実行結果", "結果行から詳細表示、コピー、保存ができます。名前付きValueTupleはItem1ではなく宣言時の要素名を表示します。Task／ValueTaskの列挙結果は1つの列挙ツリーへまとまり、各要素に元の0始まり位置が［0］のように表示されます。ワークスペースのプレビューは完了済み結果を再利用し、遅延列挙を再実行しません。現在の結果はLast、過去の結果はOut[index]で元のオブジェクトを参照できます。"),
            new("変数", "［ワークスペース］の［変数］には宣言済み変数と名前のない結果が表示されます。変数をダブルクリックするかEnterを押すと名前を入力欄へ挿入し、Ctrl+Cで値をコピーできます。"),
            new("名前のない結果", "名前のない行をダブルクリックするかEnterを押すと変数名を付けられます。元の型を利用できる場合はその型で、利用できない場合はdynamicとして扱います。不要な結果は右クリックして保持を解除できます。"),
            new("データ型を推論", "JSON、オブジェクト、またはオブジェクト列を右クリックして［データ型を推論］を選ぶと、現在値からC#型と型付き変数を生成します。元の変数は残り、生成した変数では補完を利用できます。")
        ]),
        new("結果の詳細", "複雑な値をツリーやテーブルで確認・加工します。",
        [
            new("ツリー", "プロパティや要素を展開し、検索、選択部分のコピー、全体のコピー・保存ができます。選択位置のパスもコピーできます。"),
            new("テーブル", "オブジェクトや配列は［テーブル］へ切り替えられます。列名をクリックすると昇順、降順、元の順へ切り替わり、オブジェクトや配列を含むセルはEnterまたはダブルクリックで開けます。"),
            new("列を加工", "列ヘッダーの右クリックから、抽出条件、平坦化、計算列、非表示、移動を選べます。平坦化した行には元の行へ戻るための列が追加され、計算式ではrowを現在行として補完を利用できます。"),
            new("持ち出す", "表示中の表はTSVとしてコピー、CSVとして保存できます。取得式を生成できる操作では、現在の加工結果を再現するC#式もコピーできます。")
        ]),
        new("シンボル", "名前空間、型、プロパティ、メソッドとドキュメントを探索します。",
        [
            new("検索", "名前空間、型、プロパティ、メソッド、コメント、アセンブリ名を横断検索します。Ctrl+Fで検索欄へ移動し、Enterまたは↓で先頭の一致項目へ移動します。"),
            new("概要とドキュメント", "項目へマウスを置くと署名と概要が表示されます。ポップアップへマウスを移動でき、.NET APIはMicrosoft Learnを開けます。enumを展開するとメンバーと定数値を確認できます。"),
            new("型の関係", "項目をクリックするとパラメーター、戻り値、継承元、実装インターフェース、派生型、実装型を右側に表示します。型関係をクリックすると対象へ移動できます。")
        ]),
        new("ファイルとJSON", "目的とファイルサイズに合う読み方を選べます。",
        [
            new("読み方を選ぶ", "［データ］メニューまたはファイルのドロップから、ファイル情報、JSON全体、JSONL全体、テキスト全体、行ストリーム、バイナリ全体を選べます。拡張子だけで読み方を決めないため、内容に合う項目を選んでください。"),
            new("JSON", "ReadJsonAsyncはオブジェクト、配列、スカラーを含む1つのJSON値全体をJsonNodeで返します。大きなトップレベル配列を先頭だけ確認する場合はReadJsonArrayAsyncを使います。", "var json = await Data.ReadJsonAsync(@\"C:\\data\\settings.json\", ExecutionCancellation);\nvar rows = await Data.ReadJsonArrayAsync(@\"C:\\data\\large-array.json\", 1000, ExecutionCancellation);"),
            new("JSON Lines", "JSONLは1行を1つのJSON値として扱います。ReadAllJsonLinesAsyncは全件、ReadJsonLinesAsyncは指定件数、StreamJsonLinesAsyncは保持せず順次読み込みます。", "var rows = await Data.ReadJsonLinesAsync(@\"C:\\data\\events.jsonl\", 1000, ExecutionCancellation);\nawait foreach (var row in Data.StreamJsonLinesAsync(@\"C:\\data\\events.jsonl\", ExecutionCancellation))\n{\n    // 1件ずつ処理\n}"),
            new("大きなファイル", "Inspectは内容を読まずファイル情報を返します。ReadLinesは遅延列挙です。PreviewTextとReadBytesは最大1 MiBなので、まず一部だけ確認するときに利用できます。全体読み込みはファイル全体をメモリへ保持する点に注意してください。", "Data.Inspect(@\"C:\\data\\large.json\")\nData.ReadLines(@\"C:\\data\\large.csv\").Take(100)")
        ]),
        new("保存と依存関係", "セッションの再構築とライブラリの追加を行います。",
        [
            new("ワークスペース", "［ファイル］から、選択中コンソールの入力、履歴、using、DLL参照、NuGetパッケージを.pgsworkspaceへ保存できます。実行中オブジェクトは保存せず、開くと依存関係を復元して入力を順番に再実行します。ファイル書き込みなどの副作用も再実行されます。"),
            new("NuGet", "［ワークスペース］の［NuGet］で検索し、バージョンを選んで追加します。正確なバージョンを指定するコロンコマンドも利用できます。", ":package add Humanizer.Core --version 3.0.10\n:package list"),
            new("DLLとusing", "［ライブラリ］からDLLを追加し、［using］から名前空間を追加できます。DLLは入力欄へドロップすることも、コロンコマンドで追加することもできます。", ":reference add \"C:\\Libraries\\Example.dll\"\n:using add Example.Namespace")
        ]),
        new("停止と安全性", "Worker分離は安定性のためであり、サンドボックスではありません。",
        [
            new("停止", "Esc、停止ボタン、またはコピー対象がないときのCtrl+Cで、実行前の入力解析、実行中のコード、NuGetの検索・追加を中断できます。実行前なら未実行の入力を復元します。待機や長いループにExecutionCancellationを渡すと、セッション状態を保ったまま協調停止できます。応答しないコードはWorkerを強制終了するため、Worker内の変数状態が失われます。", "await Task.Delay(10_000, ExecutionCancellation)\nExecutionCancellation.ThrowIfCancellationRequested()"),
            new("権限", "任意のC#コードとパッケージは現在のWindowsユーザー権限で動作します。信頼できないコード、DLL、パッケージを実行しないでください。")
        ])
    ];

    private static IReadOnlyList<HelpTopic> CreateEnglishTopics() =>
    [
        new("Getting started", "Run code, inspect its result, and reuse it.",
        [
            new("Run code", "Enter C# in the input editor at the bottom, then press the configured execution key. Variables, methods, types, and usings remain available in the same console.", "var values = Enumerable.Range(1, 10).ToArray();\nvalues.Where(x => x % 2 == 0)"),
            new("Reuse a result", "Results appear above the input. Last is the latest result and Out[index] is an earlier original result object. Top-level await is supported.", "await Task.FromResult(42)\nLast\nOut[1]"),
            new("Inspect it", "Double-click a result or press Enter to open Result Details. Objects and arrays can be viewed as a tree or table.")
        ]),
        new("Consoles and .NET", "Use independent execution environments for different tasks.",
        [
            new("Console tabs", "Choose + or press Ctrl+T to add a console. Each tab has its own Worker, variables, history, references, usings, and target .NET. Use Ctrl+Tab or Ctrl+Shift+Tab to switch, and Ctrl+W, ×, or middle-click to close."),
            new("Target .NET", "Choose an installed .NET SDK under Workspace > Settings. It controls completion, diagnostics, and execution. Changing it clears current variables, types, and methods while keeping the transcript, history, references, and usings.")
        ]),
        new("Input and IntelliSense", "Completion and diagnostics use the current session state.",
        [
            new("Completion and documentation", "Type a period or press Ctrl+Space to open completion, then accept with Enter, Tab, or a click. Selecting an unimported type or extension method also adds its using. Hover a symbol for its signature and summary; .NET APIs link to Microsoft Learn from the popup."),
            new("Diagnostics and focus", "Errors and warnings appear while you type. F8 or Shift+F8 moves between diagnostics, F6 or Shift+F6 cycles panes, Ctrl+L focuses input, and Ctrl+F focuses symbol search. Esc closes completion and stops an active operation. Ctrl+C copies a selection, or stops a running submission when there is no copy target."),
            new("Lines and history", "With Enter-to-run, Shift+Enter inserts a line break. With Ctrl+Enter-to-run, Enter or Shift+Enter inserts one. Use Up and Down on a single line to browse history, or click an earlier input to restore it."),
            new("Drop a file", "Drop files or folders onto the input editor to choose an action such as inserting paths, creating a data-reading expression, or listing a folder. Nothing runs automatically, and multiple files can be handled together.")
        ]),
        new("Results and variables", "Inspect results, name them later, and reuse them.",
        [
            new("Results", "A result row can be inspected, copied, or saved. Named ValueTuple elements use their declared names instead of Item1. Enumerated Task and ValueTask results share one sequence tree, and each child shows its original zero-based position as [0], for example. Workspace previews reuse completed results without re-enumerating a lazy source. Last refers to the current result, and Out[index] refers to an earlier original result object."),
            new("Variables", "Workspace > Variables shows declared variables and unnamed results. Double-click a variable or press Enter to insert its name into the editor; press Ctrl+C to copy its value."),
            new("Unnamed results", "Double-click an unnamed row or press Enter to give it a variable name. Its original type is used when available, with dynamic as the fallback. Right-click and choose Release when the object no longer needs to be retained."),
            new("Infer data type", "Right-click JSON, an object, or a sequence of objects and choose Infer data type to generate C# models and a typed variable from the current value. The original variable remains available.")
        ]),
        new("Result details", "Explore and reshape complex values as a tree or table.",
        [
            new("Tree", "Expand properties and elements, search within the value, copy a selection or path, and copy or save the complete result."),
            new("Table", "Switch objects and arrays to Table. Click a column name to cycle through ascending, descending, and original order. Press Enter or double-click an object or array cell to open it."),
            new("Transform columns", "Right-click a column header to filter, flatten, add a calculated column, hide, or move it. Flattened rows retain a link to the original row, and calculated formulas use row as the current row with completion."),
            new("Export", "Copy the visible table as TSV or save it as CSV. When supported, copy a C# expression that reproduces the current transformation.")
        ]),
        new("Symbol explorer", "Browse namespaces, types, properties, methods, and documentation.",
        [
            new("Search", "Search across namespaces, types, properties, methods, comments, and assembly names. Ctrl+F focuses search; Enter or Down moves to the first match."),
            new("Summary and documentation", "Hover an item for its signature and summary, then move into the popup to open Microsoft Learn for a .NET API. Expand an enum to inspect its members and constant values."),
            new("Type relationships", "Click an item for parameters, return values, base types, interfaces, derived types, and implementing types. Select a relationship to navigate to that type.")
        ]),
        new("Files and JSON", "Choose a loading method that matches the content and size.",
        [
            new("Choose how to read", "Use the Data menu or file drop to choose file information, complete JSON, complete JSONL, complete text, a line stream, or complete binary. The extension does not force a mode, so choose one that matches the content."),
            new("JSON", "ReadJsonAsync returns one complete JSON value—object, array, or scalar—as JsonNode. Use ReadJsonArrayAsync to inspect only the first items of a large top-level array.", "var json = await Data.ReadJsonAsync(@\"C:\\data\\settings.json\", ExecutionCancellation);\nvar rows = await Data.ReadJsonArrayAsync(@\"C:\\data\\large-array.json\", 1000, ExecutionCancellation);"),
            new("JSON Lines", "JSONL treats each line as one JSON value. ReadAllJsonLinesAsync reads all values, ReadJsonLinesAsync reads a requested count, and StreamJsonLinesAsync processes them without retaining every value.", "var rows = await Data.ReadJsonLinesAsync(@\"C:\\data\\events.jsonl\", 1000, ExecutionCancellation);\nawait foreach (var row in Data.StreamJsonLinesAsync(@\"C:\\data\\events.jsonl\", ExecutionCancellation))\n{\n    // Process one value\n}"),
            new("Large files", "Inspect returns metadata without reading content. ReadLines is lazy. PreviewText and ReadBytes are bounded to 1 MiB for sampling. Complete-read methods retain the entire file in memory.", "Data.Inspect(@\"C:\\data\\large.json\")\nData.ReadLines(@\"C:\\data\\large.csv\").Take(100)")
        ]),
        new("Saving and dependencies", "Rebuild sessions later and add libraries.",
        [
            new("Workspace", "Use File to save the selected console's input, history, usings, DLL references, and NuGet packages as a .pgsworkspace. Live objects are not serialized. Opening it restores dependencies and replays submissions, including side effects such as file writes."),
            new("NuGet", "Search under Workspace > NuGet, choose a version, and add it. Colon commands can specify an exact version.", ":package add Humanizer.Core --version 3.0.10\n:package list"),
            new("DLLs and usings", "Add a DLL under Libraries and a namespace under Usings. A DLL can also be dropped onto input or added with a colon command.", ":reference add \"C:\\Libraries\\Example.dll\"\n:using add Example.Namespace")
        ]),
        new("Cancellation and security", "Worker isolation improves recovery; it is not a sandbox.",
        [
            new("Stop", "Press Esc, Stop, or Ctrl+C when there is no copy target to cancel pre-execution analysis, running code, NuGet searches, and package installation. Cancelling analysis restores the unsubmitted input. Pass ExecutionCancellation to waits and long loops to stop cooperatively without losing session state. A non-responsive Worker is terminated, so Worker variables are lost.", "await Task.Delay(10_000, ExecutionCancellation)\nExecutionCancellation.ThrowIfCancellationRequested()"),
            new("Permissions", "Submitted code and packages run with your current Windows user permissions. Never run untrusted code, DLLs, or packages.")
        ])
    ];
}
