## 변경 사항 (v1.2.0)

- **두 창과 비교 분리**: `Ctrl+D`로 한 창 ↔ 두 창, `Ctrl+M`으로 비교 켜기/끄기. 두 창은 비교 표시 없는 일반 탐색 창 두 개이고, 비교는 그 위에서 따로 켭니다 (비교를 켜면 두 창도 자동으로 켜지고, 두 창을 끄면 비교도 꺼짐)
- **두 창에서 복사·이동**: `Ctrl+→/←` 복사, `Ctrl+Shift+→/←` 이동(신규), 툴바에 복사/이동 버튼
- **창 사이 끌기**: 기본 복사, `Shift`+끌기는 이동 (같은 창 안이나 탐색기에서 끌어온 드롭은 기존 규칙 유지)
- 비교를 끈 뒤에도 차이 색이 남던 문제, 두 창을 켜도 복사 버튼이 비활성으로 남던 문제 수정
- 이전 버전에서 두 창으로 종료했다면 이번부터 두 창(비교 꺼짐)으로 시작합니다

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.2.0-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.2.0-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
