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
| `tests/SideBySide.Core.Tests` | `net8.0` | Core 단위 테스트 (xUnit). |

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

### 비교 모드
- `CompareViewModel.Enter(left, right)` — 두 탭의 `NavigationInterceptor`를 설정해 탐색을 가로챔 (한쪽 이동 → 반대쪽도 같은 상대 경로로)
- `FolderComparer.CompareLevelAsync` — 현재 폴더 한 단계 열거·병합·파일 판정 (빠름) → 화면 표시
- `FolderComparer.ResolveDirectoriesAsync` — 하위 폴더 상태를 병렬 재귀 계산, 행마다 콜백 → UI 갱신. 결과는 캐시
- 좌우 목록은 같은 인덱스에 같은 이름이 오도록 빈 행(`FileItemViewModel.Placeholder`)을 채워 정렬. 선택·스크롤은 인덱스로 동기화

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

### 색/테마 변경
- 색은 `Themes/Tokens.Light.xaml`, `Themes/Tokens.Dark.xaml`에만 정의 (두 파일의 키는 같아야 함)
- 컨트롤 스타일(`Themes/Controls/*.xaml`)은 `DynamicResource`로만 색을 참조 → `ThemeManager`가 토큰 사전만 교체

### 설정 항목 추가
- `AppSettings`에 기본값과 함께 속성 추가
- 형식이 바뀌면 `AppSettings.CurrentVersion`을 올리고 `JsonSettingsStore.Migrate`에 단계 추가 (테스트 포함)

### 셸 기능 추가
- `Shell/Abstractions`에 인터페이스 → `Shell/Services`에 구현 → `App.ConfigureServices`에 등록

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

UI 컬렉션 변경은 항상 UI 스레드에서 한다 (`await` 후 컨텍스트 복귀 또는 `Dispatcher.BeginInvoke`).
