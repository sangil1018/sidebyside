## 변경 사항 (v1.6.1)

- **고침**: 두 창 모드에서 파일 목록에 포커스가 있을 때 `Ctrl+←/→`(반대쪽 창으로 복사)와 `Ctrl+Shift+←/→`(이동) 단축키가 동작하지 않던 문제

v1.6.0의 새 기능(Git 패널, 좌우 창 바꾸기, 선택된 창 표시)은 [v1.6.0 릴리스](https://github.com/sangil1018/sidebyside/releases/tag/v1.6.0)를 참고하세요.

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.6.1-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.6.1-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정 (도구는 **모듈** 탭에서 켜기)
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
