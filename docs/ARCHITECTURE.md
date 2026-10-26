# 아키텍처

## 레이어와 의존 규칙

```
SideBySide.App  (WPF, MVVM)        ──▶  SideBySide.Shell (Win32/COM)  ──▶  SideBySide.Core (순수 .NET)
       └────────────────────────────────────────────────────────────────▶
```

| 프로젝트 | 대상 | 규칙 |
|---|---|---|
| `SideBySide.Core` | `net8.0` | UI·P/Invoke 금지. 모든 OS에서 빌드·테스트 가능해야 함. 로직은 가능하면 여기에. |
| `SideBySide.Shell` | `net8.0-windows` | Windows 셸 래퍼만. 앱은 `Abstractions/` 인터페이스로만 사용. P/Invoke 선언은 `Interop/NativeMethods.cs` 한 곳. |
| `SideBySide.App` | `net8.0-windows` (WPF) | 뷰 + 뷰모델. 코드 비하인드는 순수 UI 동작(포커스, 드래그, 스크롤)만. |
| `SideBySide.Modules.Abstractions` | `net8.0-windows` (WPF) | 모듈 계약(`IToolModule`, `IModuleHost`), 공통 화면 골격(`ToolPanel`·`ToolSection`·`ToolRow`), 아이콘 표(`Glyphs`). 앱을 참조하지 않는다. |
| `Modules/SideBySide.Modules.*` | `net8.0-windows` (WPF) | 켜고 끄는 도구 모듈 6개. Abstractions·Core만 참조. 색·스타일을 직접 정의하지 않고 앱 리소스를 쓴다. |
| `tests/SideBySide.Core.Tests` | `net8.0` | Core 단위 테스트 (xUnit). |
| `tests/SideBySide.Modules.Tests` | `net8.0-windows` | 모듈·승격 헬퍼 테스트, 모든 화면을 실제 리소스로 만들어 보는 UI 스모크 테스트. |

공통 빌드 설정(`Nullable`, `TreatWarningsAsErrors` 등)은 `Directory.Build.props`, 코드 스타일은 `.editorconfig`.

## 주요 흐름

### 탐색기
- `MainViewModel` — 모드/레이아웃 상태, 모든 명령 등록
- `PaneViewModel` — 좌/우 창, 탭 목록
- `TabViewModel` — 폴더 하나: 열거(`DirectoryLoader`) → 필터(`FilterSet`) → 정렬 → `BulkObservableCollection.ReplaceAll` (Reset 1회). 방문 기록, `FileSystemWatcher` 자동 새로 고침(300ms 디바운스)
- `FileItemViewModel` — 행. 아이콘/형식 이름은 화면에 보일 때 지연 요청
- `ExplorerContext` — 모든 탭이 공유하는 필터·잘라내기 상태와 셸 서비스

- 보기 방식: `FilePane.ApplyView`가 `GridView`(자세히) ↔ `VirtualizingWrapPanel` + 타일 템플릿(아이콘)을 교체. 같은 ListView라 선택·드래그·이름 바꾸기 코드는 공통
- 하위 폴더 검색: `TabViewModel.RunRecursiveSearchAsync` → `DirectoryLoader.Search`(필터로 제외된 폴더는 들어가지 않음, 링크는 따라가지 않음)를 백그라운드에서 돌리고 150ms마다 결과를 목록에 추가
- 실행 취소: `UndoService` 스택. `MainViewModel`이 작업마다 되돌리기 동작을 기록. 복사/이동은 작업 전후 대상 폴더 목록을 비교해 새로 생긴 항목만 대상으로 함. 휴지통 복원은 `RecycleBinStore`가 `$Recycle.Bin\<SID>`의 `$I` 메타데이터를 읽어 `$R` 파일을 원래 위치로 이동

### 두 창(분할)과 비교
- 상태는 둘: `MainViewModel.IsDualPane`(두 창)과 `IsCompareMode`(비교). 비교 켜짐 ⇒ 두 창 켜짐. `OnIsCompareModeChanged`가 `Compare.Enter/Exit`를 부르고, 두 창을 끄면 비교도 함께 끈다
- 두 창만 켠 상태는 일반 탭 두 개 — `Ctrl+→/←` 복사, `Ctrl+Shift+→/←` 이동은 `CopyBetweenPanesAsync(from, to, move)`. 창 사이 끌기는 `FilePane`이 시작 창을 `MainViewModel.DragSourcePane`에 기록해 두고 `ComputeDropEffect`에서 기본 복사(Shift 이동)로 결정
- 비교를 끄면 `Compare.Exit`가 비교 표시를 지우고 두 탭을 일반 목록으로 다시 불러옴
- 한쪽이 "내 PC"면 `CompareViewModel.RunAsync`가 각 탭을 일반 목록으로 로드하고, 탐색으로 양쪽이 폴더가 되면 자동으로 비교
- `CompareViewModel.Enter(left, right)` — 두 탭의 `NavigationInterceptor`를 설정해 탐색을 가로챔 (한쪽 이동 → 반대쪽도 같은 상대 경로로)
- `FolderComparer.CompareLevelAsync` — 현재 폴더 한 단계 열거·병합·파일 판정 (빠름) → 화면 표시
- `FolderComparer.ResolveDirectoriesAsync` — 하위 폴더 상태를 병렬 재귀 계산, 행마다 콜백 → UI 갱신. 결과는 캐시
- 좌우 목록은 같은 인덱스에 같은 이름이 오도록 빈 행(`FileItemViewModel.Placeholder`)을 채워 정렬. 선택·스크롤은 인덱스로 동기화

### 파일 작업 / 진행률
- `Core/FileSystem/FileTransfer` — 복사·이동 엔진. 사전 스캔(전체 바이트) → 진행률·속도·남은 시간 보고, 작은 파일은 `File.Copy`, 큰 파일은 1MB 버퍼 스트림, 시각·속성 보존, 같은 볼륨 이동은 즉시, 폴더 병합, 충돌 콜백, 항목별 오류 수집, 취소 시 부분 파일 삭제
- `Services/FileTransferService` — 엔진을 스레드 풀에서 실행, 충돌 질문은 UI 스레드 대화상자로 마샬링, `Operations` 컬렉션(`OperationViewModel`)으로 하단 작업 줄에 노출. 휴지통 삭제 같은 셸 작업은 `RunIndeterminateAsync`
- `MainViewModel.UpdateOperationIndicators` — 대상 폴더를 보는 탭의 `OperationProgress`(주소 표시줄 채움)와 `TaskbarItemInfo` 진행률 갱신
- 셸(`IFileOperations`)은 휴지통 삭제·이름 바꾸기만 담당 (`FOF_SILENT`로 셸 진행 창 없음)

### 영역 선택 (FilePane)
- 빈 곳 또는 이름 밖(`Tag="DragHandle"`이 아닌 곳)에서 끌기 → 사각형 오버레이. 가상화로 화면 밖 행은 컨테이너가 없으므로 패널 내용 좌표(스크롤 포함)로 인덱스를 계산: 자세히 = 행 높이, 아이콘 = `VirtualizingWrapPanel.IndicesIntersecting`

### USB 장치 제거
- `Shell/Services/DriveService` — 꺼낼 수 있는지(이동식·광학, 또는 `IOCTL_STORAGE_QUERY_PROPERTY` 버스 종류가 USB) 판별, 셸 `eject` 동사 실행 후 드라이브가 사라지는지 확인
- `MainWindow`가 `WM_DEVICECHANGE`를 받아 `MainViewModel.OnDevicesChangedAsync`로 드라이브 목록·탭 갱신

### 파일 비교
- `TextFileCodec` — BOM → 엄격 UTF-8 → 시스템 ANSI 순 인코딩 판별, 줄끝·BOM 보존 저장(임시 파일 후 교체)
- `TextDiff` / `Myers` — 선형 메모리 Myers 차이(라인) + 글자 단위 강조
- `DiffDocument` — 블록 적용, 전체 텍스트 교체, 실행 취소 스택
- `FileDiffViewModel` / `FileDiffView` — 가상화 ListBox, `DiffText` 첨부 속성이 강조 Run 생성. 긴 줄은 텍스트를 Canvas에 넣고 공용 `HorizontalOffset`만큼 옮겨 좌우가 함께 가로 스크롤

## 확장 방법

### 새 명령(단축키) 추가
1. `Commands/CommandIds.cs`에 ID 추가
2. `Commands/CommandRegistry.cs`에 제목·단축키·범위 한 줄 추가
3. `MainViewModel.RegisterCommands()`에서 동작 등록
4. 버튼이 필요하면 XAML에서 `Command="{cmd:Command Id}" ToolTip="{cmd:Tip Id}"`

범위(`CommandScope`): `Global`(창 전체) / `FileList`(파일 목록 포커스 — 텍스트 입력 중 Ctrl+C 등이 입력란으로 가도록) / `Diff`(파일 비교 화면).
`canExecute`가 false면 키가 처리되지 않고 컨트롤 기본 동작으로 넘어간다 (예: 한 창 모드에서 Tab).

### 화면 문자열 (한국어 / 영어)
- 원문(한국어)을 그대로 키로 쓴다: C#은 `L.T("새 탭")`, 값이 들어가면 `L.F("항목 {0:N0}개", count)`, XAML은 `Text="{l:T '새 탭'}"` (`xmlns:l="clr-namespace:SideBySide.App.Infrastructure"`).
- 영어는 `Core/Localization/L.English.cs` 표에 한 줄 추가한다. 없으면 한국어가 그대로 보인다. `LocalizationTests`가 자리표시자({0} 등) 개수가 같은지 검사한다.
- 언어는 시작할 때 `App.OnStartup`에서 `L.Use(settings.Language)`로 한 번 정한다 (명령 표·XAML이 그 뒤에 읽히므로 바꾸면 다시 시작해야 적용).
- 문장을 조각으로 이어 붙이지 말 것 (어순이 달라 번역할 수 없다). 경우마다 완성된 문장을 둔다.

### 단축키 변경 (사용자)
- 기본값은 `CommandRegistry.Defaults`. 사용자가 바꾼 것만 `AppSettings.KeyBindings`(명령 ID → `"Ctrl+Shift+Right"`, 빈 값 = 없음)에 저장하고 `CommandRegistry.ApplyOverrides`가 `All`에 덮어쓴다. 바꾼 명령은 보조 단축키까지 새 키 하나로 대체한다.
- `CommandHost.BindKeys`가 붙인 바인딩을 기억해 두고 `RebindAll`로 바로 다시 붙인다. 겹침 검사는 `CommandRegistry.Compose` + `ScopesOverlap`(같은 범위이거나 한쪽이 창 전체).

### 색/테마 변경
- 색은 `Themes/Tokens.Light.xaml`, `Themes/Tokens.Dark.xaml`에만 정의 (두 파일의 키는 같아야 함)
- 컨트롤 스타일(`Themes/Controls/*.xaml`)은 `DynamicResource`로만 색을 참조 → `ThemeManager`가 토큰 사전만 교체

### 설정 항목 추가
- `AppSettings`에 기본값과 함께 속성 추가
- 형식이 바뀌면 `AppSettings.CurrentVersion`을 올리고 `JsonSettingsStore.Migrate`에 단계 추가 (테스트 포함)

### 포인트(강조) 색
- `AppSettings.AccentColor`: `Default`(토큰 그대로) / `System`(Windows 강조색) / `#RRGGBB`
- `ThemeManager`가 토큰 사전 바로 뒤에 강조색 사전을 넣어 `Brush.Accent`·`AccentText`·`AccentSubtle`·`Selected`·`Newer`·`DiffLeftMark`만 덮어쓴다. 명도 보정·대비 계산은 `Core/Theming/AccentPalette.cs`

### 셸 기능 추가
- `Shell/Abstractions`에 인터페이스 → `Shell/Services`에 구현 → `App.ConfigureServices`에 등록

## 도구 모듈

### 수명과 메모리
- `App/Modules/ModuleCatalog`: 모듈 정적 정보(이름·아이콘·우클릭 적용 규칙). 모듈 형식은 `[MethodImpl(NoInlining)]` 팩토리에서만 참조 → **사용자가 모듈을 처음 열 때 그 어셈블리가 처음 로드된다.** 꺼진 모듈·열지 않은 모듈은 메모리에 없다 (시작 시 로드되는 것은 작은 Abstractions뿐).
- `ModuleManager`: 켜기/끄기, 처음 열 때 생성, 오른쪽 도구 패널(`Views/ToolPanelHost`) 또는 별도 창(`Views/ModuleWindow`)에 표시. 화면을 닫으면 `OnClosed` → 백그라운드 서비스가 없으면 `Dispose`. 설정에서 끄면 즉시 닫고 해제.
- 모듈 데이터는 `AppSettings.Modules[id].Data`(JsonObject, 256KB 상한)에 `IModuleHost.LoadData/SaveData`로 저장.

### 진입점
- 셸 우클릭 메뉴: `ShellContextMenu`가 `QueryContextMenu` 뒤에 앱 항목(`ShellMenuItem`)을 맨 위에 넣는다. 명령 ID는 셸 확장 범위(1..0x7FFF) 밖 0x8000부터, 아이콘은 같은 글리프를 메뉴 글자색으로 그린 32비트 비트맵.
- 툴바 '도구 ▾'(켜진 모듈이 있을 때만), 명령 `Module.<id>`(단축키 지정 가능).

### 새 모듈 추가
1. `src/Modules/SideBySide.Modules.<이름>` 프로젝트 (다른 모듈 csproj 복사) → 솔루션·App 참조 추가
2. `ToolModuleBase`를 상속해 `CreateView`·`Open` 구현. 화면은 `m:ToolPanel` 골격 + `m:ToolSection`/`m:ToolRow`, 색은 `DynamicResource` 토큰만
3. `ModuleCatalog.All`에 디스크립터 + `NoInlining` 팩토리 한 줄
4. 화면 문자열 영어는 `Core/Localization/L.English.Modules.cs`

### 관리자 권한 작업 (승격 헬퍼)
- 일반 권한 앱이 GUID 이름의 파이프(현재 사용자 전용, 인스턴스 1개)를 먼저 만들고 같은 exe를 `--elevated <작업> <파이프> <SHA-256>`로 UAC 실행 (`Modules/Elevation.cs`).
- 승격 프로세스는 파이프로 입력을 받아 명령줄 해시와 대조하고, 디스크립터의 `ElevatedOperations`에 등록된 작업만 실행한다. 파일을 쓰지 않는다 (사용자 쓰기 가능 경로 바꿔치기 차단).
- 작업은 입력을 다시 검증한다: 임시 파일 정리는 시스템 카테고리 **ID**만 받고, LAN 공유는 실제 어댑터 인덱스·고정 IP·허용 폴더(링크 없음)만, 명령은 셸 없이 System32 exe + 인자 목록.

### 독립 앱 호환
- Direct Connect는 AgentZi Direct Connect(Flutter) 앱과 와이어 호환 (`Modules.DirectConnect/Protocol.cs` 머리 주석). 탐색은 58732 수신 소켓(SO_REUSEADDR) + 핑 전용 임시 포트 소켓 2개라 같은 PC의 독립 앱과 공존한다.
- LAN 공유는 lan_share_setup과 같은 IP·DNS·공유 이름·기본 폴더·설명을 쓴다. 방화벽 그룹은 언어 무관 간접 이름(`@FirewallAPI.dll,-28502`, `-32752`).

## 스레딩

| 작업 | 위치 |
|---|---|
| 디렉터리 열거, 필터·정렬, 뷰모델 생성 | 스레드 풀 (`Task.Run`) |
| 폴더/파일 비교 | `Parallel.ForEachAsync` (코어 수) |
| 아이콘 추출 | 전용 STA 스레드 1개 (`ShellIconProvider`) |
| 썸네일(큰 아이콘) | 전용 STA 스레드 2개, 최근 요청 우선 |
| 하위 폴더 검색 | 스레드 풀 (`Task.Run`), UI는 150ms마다 결과 반영 |
| 셸 파일 작업 (SHFileOperation) | 작업마다 STA 스레드 |
| 미리보기 처리기, 우클릭 메뉴, 클립보드 | UI 스레드 (COM STA 요구) |
| 이미지 크기 조정 | `Parallel.ForEachAsync` (코어 수), WIC 디코더 단계 축소 |
| 시퀀스 뷰어 디코드 | 전용 스레드 (코어 수 - 1, 낮은 우선순위), 최신 요청 우선, 메모리 예산 LRU |
| 시퀀스 뷰어 합성 | 모두 Normal이면 WPF(GPU) 레이어 합성, 특수 블렌드는 `Parallel.For` 행 단위 CPU 합성 |
| 임시 파일 검사 | 카테고리마다 스레드 풀 동시 실행 |
| Direct Connect | 비동기 소켓 (UDP 탐색 2개, TCP 받기 서버·연결별 작업) |

UI 컬렉션 변경은 항상 UI 스레드에서 한다 (`await` 후 컨텍스트 복귀 또는 `Dispatcher.BeginInvoke`).
