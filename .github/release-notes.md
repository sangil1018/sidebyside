## 변경 사항 (v1.4.1)

- **마우스 뒤로/앞으로 버튼**: 마우스의 추가 버튼으로 이전/다음 폴더로 이동합니다. 두 창이면 마우스가 있는 창에 적용됩니다.
- **하단 진행 표시**: 얇은 슬라이더 대신 작업 줄 전체가 진행률만큼 색으로 채워집니다. 퍼센트 표시는 그대로이며, 진행률을 알 수 없는 작업은 띠가 흐릅니다.

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.4.1-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.4.1-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
