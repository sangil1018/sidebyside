## 변경 사항 (v1.3.1)

- **앱이 꺼져 있어도 창 열기 단축키 사용**: 환경설정의 "SideBySide가 꺼져 있어도 단축키 사용"을 켜면 Windows 로그인 시 창 없이 대기하다가, `Win+Alt+E`(또는 선택한 키)를 누르면 SideBySide를 열거나 열려 있는 창을 앞으로 가져옵니다. 끄면 자동 실행과 대기 프로세스를 정리합니다.
- **탐색기 우클릭 "새 창에서 열기"**: 기본 파일 관리자를 켜면 이 메뉴도 SideBySide로 엽니다. v1.3.0에서 이미 켜 두었다면 새 버전을 처음 실행할 때 자동으로 추가됩니다.
- 휴지통·제어판 같은 가상 폴더는 탐색기로 넘기고, "내 PC"는 SideBySide에서 엽니다.

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.3.1-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.3.1-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
