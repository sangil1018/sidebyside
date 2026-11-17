## 변경 사항 (v1.6.0)

### Git 패널 (새 도구 모듈)
**설정 > 모듈**에서 **Git**을 켜면, 한 창 모드로 git 저장소(clone한 폴더)에 들어갈 때 오른쪽에 Git 패널이 자동으로 열립니다. 저장소 밖으로 나가면 닫히고, 폴더 우클릭이나 **도구 ▾**로 직접 열 수도 있습니다.

- **변경**: 변경 파일 목록에서 체크한 파일만 커밋, 줄 단위 diff, 변경 취소(새 파일은 휴지통으로), 요약·설명 입력 후 커밋 (`Ctrl+Enter`)
- **브랜치**: 전환, 새 브랜치 만들기, 원격 브랜치 받아 오기
- **동기화**: 상태에 따라 Fetch / Pull / Push / 브랜치 게시 단추 하나로
- **기록**: 커밋 목록(아직 올리지 않은 커밋 표시)과 커밋별 변경 파일·diff
- 시스템에 설치된 git을 그대로 사용해 기존 로그인·SSH 설정이 적용됩니다 ([Git for Windows](https://git-scm.com/download/win) 필요)

### 두 창
- **좌우 창 바꾸기**: `Ctrl+U` 또는 도구 모음의 ⇄ 단추로 두 창의 탭을 통째로 맞바꿉니다. 비교 중이면 바꾼 뒤 다시 비교합니다
- **선택된 창 표시**: 선택된 창의 활성 탭을 포인트 색 배경·굵은 글자로, 탭 표시줄 아래에 포인트 색 선으로 강조하고 반대쪽 창의 탭·탐색 줄은 흐리게 표시합니다. 한 창 모드에서도 활성 탭이 같은 방식으로 강조됩니다

### 고침
- 큰 아이콘 보기에서 폴더·이미지 미리보기가 위아래로 뒤집혀 보이던 문제

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.6.0-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.6.0-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정 (도구는 **모듈** 탭에서 켜기)
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
