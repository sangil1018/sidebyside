## 변경 사항 (v1.7.0)

### SVN 패널 (새 도구 모듈)
**설정 > 모듈**에서 **SVN**을 켜면, 한 창 모드로 SVN 작업 사본 폴더에 들어갈 때 오른쪽에 SVN 패널이 자동으로 열립니다. 작업 사본 밖으로 나가면 닫히고, 폴더 우클릭이나 **도구 ▾**로 직접 열 수도 있습니다.

- **변경**: 변경 파일 목록에서 체크한 파일만 커밋 (새 파일은 자동으로 추가, 디스크에서 지운 파일은 저장소에서도 삭제), 줄 단위 diff, 변경 취소(새 파일은 휴지통으로), 요약·설명 입력 후 커밋 (`Ctrl+Enter`)
- **업데이트**: 서버의 새 리비전 받기. 새로 고침 단추로 받을 변경 수를 확인하고, 충돌은 '충돌'로 표시합니다
- **브랜치**: trunk / branches / tags 배치에서 브랜치 전환(`svn switch`)과 새 브랜치 만들기
- **기록**: 리비전 목록과 리비전별 변경 파일·diff
- 옵션에서 작업 사본 정리(cleanup)
- 시스템에 설치된 svn을 그대로 사용해 저장된 로그인이 적용됩니다 ([TortoiseSVN](https://tortoisesvn.net/)을 설치할 때 'command line client tools'를 함께 설치하거나 SlikSVN 필요)

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.7.0-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.7.0-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창, `Ctrl+M`으로 비교, `Ctrl+,`로 환경설정 (도구는 **모듈** 탭에서 켜기)
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
