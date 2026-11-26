## 변경 사항 (v1.7.3)

### 개선
- **비교 모드 복사는 다르고 더 최신인 파일만**: 비교 중에 한쪽 창의 항목을 반대쪽으로 복사하면(**오른쪽 창으로 복사** `Ctrl+→` / **왼쪽 창으로 복사** `Ctrl+←`), 선택한 폴더를 통째로 복사하지 않습니다. 하위까지 비교 기준(필터·비교 옵션)으로 다시 확인해서 아래 파일만 같은 상대 경로로 복사합니다.
  - 반대쪽과 내용이 다르고 **이쪽이 더 최신인 파일**: 묻지 않고 덮어씁니다.
  - 반대쪽에 없는 파일·폴더: 그대로 복사합니다 (빈 폴더 포함).
  - 반대쪽이 더 최신이거나 수정 시각이 같은 파일은 건너뛰고, 건너뛴 개수를 알려 줍니다.
  - 비교 모드의 이동은 지금처럼 선택한 항목 전체를 옮깁니다.

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.7.3-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.7.3-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정 (도구는 **모듈** 탭에서 켜기)
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
