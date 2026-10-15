## 변경 사항 (v1.4.0)

- **환경설정 창 새 구성**: 왼쪽 목록(일반 · 탐색 창 · 단축키)과 섹션 카드로 정리
- **언어 선택**: 한국어(기본) / English. 다시 시작하면 적용됩니다.
- **빠른 실행에 시스템 위치**: 휴지통(기본 표시)·제어판·네트워크·Windows 설정을 탐색 창에 표시할 수 있습니다 (Windows 탐색기 또는 설정으로 엶).
- **단축키 보기·바꾸기**: 모든 명령의 단축키를 목록으로 보고, 키 조합을 눌러 바꾸거나 없애고 기본값으로 되돌릴 수 있습니다. 다른 명령과 겹치면 알려 줍니다. 확인을 누르면 바로 적용됩니다.

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.4.0-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.4.0-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
