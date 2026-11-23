# 테스트와 동작 확인

SideBySide는 화면을 직접 보지 않고도 앱을 띄워 조작·검사할 수 있다. 아래 방법은 모두 **사용자의 설정 파일과 분리**돼 있다.
또 **마우스·키보드를 쓰지 않으므로** PC를 쓰는 중에 실행해도 된다.

| 방법 | 실행 | 다루는 것 | 속도 |
|---|---|---|---|
| 단위 테스트 | `dotnet test tests/SideBySide.Core.Tests`, `tests/SideBySide.Modules.Tests` | Core 로직, 모듈 출력 해석, 모든 화면 생성(UI 스모크) | 수 초 |
| 시나리오 테스트 | `dotnet test tests/SideBySide.Scenarios.Tests` | 앱 전체를 테스트 프로세스 안에 띄워 명령으로 조작 (두 창·비교·복사·창 바꾸기·Git/SVN 패널 자동 열기) | 수 초 |
| UIA 드라이버 | `SideBySide.UiDriver run <exe> --scenario …` | 실제 exe를 UI Automation으로 조작 (빌드 결과물 그대로) | 시나리오당 수 초 |

## 환경 변수 (`Core/AppPaths.cs`)

| 변수 | 효과 |
|---|---|
| `SIDEBYSIDE_DATA_DIR=<폴더>` | 설정(`settings.json`), 오류 로그, 진단 로그를 이 폴더에 둔다. 평소 위치(`%APPDATA%`, `%LOCALAPPDATA%`)는 건드리지 않는다. |
| `SIDEBYSIDE_AUTOMATION=1` | 창을 화면 밖에 포커스 없이 띄운다(작업 표시줄에도 안 보임). 전역 단축키·단축키 에이전트·기본 파일 관리자 재등록을 하지 않는다. 창 위치를 저장하지 않는다. 창이 전경을 가져가면 사용자 창으로 바로 돌려준다. |
| `SIDEBYSIDE_DIAG=1` | 진단 로그(`diag.log`)를 쓴다. 쓰는 내용은 아래 '진단 로그' 절 참고. |

## 시나리오 테스트 (`tests/SideBySide.Scenarios.Tests`)

`AppHarness`는 다음 순서로 앱을 띄운다.
1. 임시 데이터 폴더에 시작 설정을 쓴다.
2. `App.ConfigureServices`로 서비스를 만든다. 모달 대화상자 대신 정해 둔 답을 하는 `FakeDialogs`를 끼워 넣는다.
3. 메인 창을 화면 밖에 띄운다.

WPF `Application`은 프로세스에 하나뿐이라, 모든 시나리오가 한 UI 스레드(`UiThread`)를 차례로 쓴다.

**도우미**
- `Execute(CommandIds.X)`: 명령 실행 (단축키·단추와 같은 경로)
- `SelectAsync` / `SelectAllAsync`: 화면 목록을 통해 선택 (`SelectAllAsync`는 사용자의 Ctrl+A와 같다)
- `WaitUntilAsync(조건)`: 조건이 될 때까지 대기
- `AssertResponsiveAsync`, `StayResponsiveAsync`: 입력과 같은 우선순위의 작업이 제한 시간 안에 처리되는지 확인한다. **화면은 그려지지만 클릭·키가 먹히지 않는 멈춤**을 잡는다.

**새 시나리오를 쓰는 방법**
1. `UiThread.Run(async () => { … })` 안에서 `AppHarness.StartAsync(설정)`로 시작하고 `using`으로 정리한다.
2. 파일은 `TempFolder`로 만든다.
3. 고친 버그는 **그 버그가 다시 생기면 실패하는지** 확인한다. 수정을 잠시 되돌려 실행해 보면 된다.

## UIA 드라이버 (`tools/SideBySide.UiDriver`)

```
dotnet build tools/SideBySide.UiDriver
tools/SideBySide.UiDriver/bin/Debug/net8.0-windows/SideBySide.UiDriver.exe inspect <SideBySide.exe> [--depth N]
tools/SideBySide.UiDriver/bin/Debug/net8.0-windows/SideBySide.UiDriver.exe run <SideBySide.exe> --scenario smoke|compare-copy
```

드라이버는 exe를 위 세 환경 변수로 띄운다. 조작은 UI Automation 패턴으로만 한다.
- 단추: Invoke
- 주소 표시줄: Value
- 목록 항목·모드: SelectionItem
- 비교: Toggle

**출력과 결과**
- 단계마다 결과를 출력하고, 앱이 전경 창을 가져갔는지도 확인한다.
- 끝나면 진단 로그의 UI 지연·반복 경고를 요약한다.
- 하나라도 실패하면 종료 코드가 1이다.

**요소 찾기**
- 명령이 붙은 컨트롤은 자동화 ID가 `cmd.<명령 ID>`다. 예: `cmd.CopyToLeft`, `cmd.SwapPanes`. `{cmd:Command}` 마크업 확장이 자동으로 단다.
- 그 밖의 ID:
  - `Mode.Single`, `Mode.Dual`, `ToolsMenu`, `Tool.<모듈 ID>`
  - `LeftPane` / `RightPane` 안의 `Address`, `FileList`
  - `Git.*` / `Svn.*` (`Refresh`, `Sync`, `Commit`, `Changes`)
- 파일 목록 항목의 이름은 파일 이름이다.
- 전체 트리는 `inspect`로 볼 수 있다.

**한계**
- 키보드 단축키 자체(실제 키 입력)는 드라이버로 확인하지 않는다. 단축키가 어떤 명령으로 가는지는 시나리오 테스트에서 확인한다(`FileListView.IsWindowShortcut`).
- UI Automation 패턴을 호출하는 순간 Windows가 대상 앱에 전경 권한을 줄 수 있다. 앱은 그 즉시 사용자 창으로 전경을 돌려준다. 측정해 보면 0.1초 미만이었다.

## 진단 로그 (`SIDEBYSIDE_DIAG=1`)

- **`[ui] 지연 N ms` / `응답 없음`**: 250ms마다 입력 우선순위로 빈 작업을 넣어 측정한다. 200ms를 넘으면 기록한다.
- **`[calls] 이름=횟수`**: 1초 단위로 집계한다. 초당 200회를 넘으면 `⚠ 반복 의심`을 붙인다.
  - 계수 지점: 선택 동기화, 비교 실행·결과 표시, 탭 새로 고침, 창 상태 변경
  - 코드에서 `Diag.Hit("이름")`로 계수 지점을 더할 수 있다.
- **`[automation] …`**: 자동화 인스턴스가 전경을 가져왔다가 돌려준 기록.

사용자 환경에서 멈춤이 재현될 때는 `SIDEBYSIDE_DIAG=1`로 앱을 실행한다. 그 뒤 `%LOCALAPPDATA%\SideBySide\diag.log`를 보면 어느 동작이 반복되는지 바로 보인다.
