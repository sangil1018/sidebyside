## 변경 사항 (v1.7.2)

### 개선
- **진단 기록**: 환경 변수 `SIDEBYSIDE_DIAG=1`로 실행하면 화면이 입력을 늦게 처리한 순간(`[ui] 지연`)과 비정상적으로 반복되는 동작(`⚠ 반복 의심`)을 `%LOCALAPPDATA%\SideBySide\diag.log`에 기록합니다. 멈춤·느려짐을 겪을 때 원인 위치를 바로 찾을 수 있습니다.
- **화면 낭독기·UI 자동화 지원**: 아이콘만 있는 단추에 명령 이름이, 파일 목록 항목에 파일 이름이 붙고, 주소 표시줄을 값 입력으로 다룰 수 있습니다.

### 개발
- 앱 전체를 화면 밖에 띄워 조작하는 시나리오 테스트와, 실제 exe를 마우스·키보드 없이 조작하는 UI 자동화 드라이버를 추가했습니다 (`docs/TESTING.md`). 사용자 설정과 분리된 자동화 모드(`SIDEBYSIDE_DATA_DIR`, `SIDEBYSIDE_AUTOMATION`)로 실행됩니다.

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.7.2-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.7.2-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정 (도구는 **모듈** 탭에서 켜기)
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
