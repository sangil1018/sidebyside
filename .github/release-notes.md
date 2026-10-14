## 변경 사항 (v1.3.0)

- **환경설정 창** (툴바 톱니 아이콘 또는 `Ctrl+,`): 테마, 기본 파일 관리자, 창 열기 단축키를 한 곳에서 설정
- **기본 파일 관리자**: 켜면 다른 프로그램이 폴더를 열 때 SideBySide를 사용합니다. 끄면 원래 설정으로 되돌립니다. 관리자 권한은 필요 없습니다.
- **창 열기 단축키**: 기본 `Win+Alt+E`, `Win+Ctrl+E` / `Win+Shift+E` 중 선택. 앱이 실행 중일 때 창을 앞으로 가져옵니다. 다른 프로그램이 같은 키를 쓰면 상태 표시줄에 알립니다.
- Win+E와 Win+D는 Windows가 예약해서 사용할 수 없습니다.

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.3.0-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.3.0-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
