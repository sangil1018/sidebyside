## 변경 사항 (v1.1.0)

- **한 창 = 탐색기, 두 창 = 비교**: 별도 모드 전환 없이 창 개수가 곧 모드입니다. `Ctrl+D`(또는 `Ctrl+M`)로 창을 하나 더 열면 좌우 비교, 한쪽이 "내 PC"면 일반 목록으로 보이다가 양쪽 모두 폴더를 열면 자동으로 비교합니다. 두 창에서도 탐색 창·미리보기를 쓸 수 있습니다.
- **`Ctrl+→` / `Ctrl+←`**: 두 창이 열려 있으면 어디서든 선택 항목을 반대쪽 창으로 복사
- **진행률을 별도 창 없이 표시**: 화면 하단 작업 줄(현재 파일·속도·남은 시간·취소), 주소 표시줄 배경, Windows 작업 표시줄 아이콘. 같은 이름 파일이 있으면 덮어쓰기 / 둘 다 유지 / 건너뛰기를 고르고 "나머지 모두에 적용"
- **마우스로 끌어서 영역 선택**: 빈 곳이나 날짜·크기 열에서 끌기. `Ctrl`/`Shift`를 누르면 기존 선택에 추가, 아이콘 보기에서도 동작
- **USB 안전하게 제거**: 탐색 창 드라이브 옆 ⏏ 버튼, 우클릭 "꺼내기", `Ctrl+Shift+E`. 장치를 꽂거나 뽑으면 드라이브 목록이 자동 갱신
- 복사·이동 시 수정/생성 시각 보존(복사 후 비교하면 "같음"), 같은 볼륨 이동은 즉시

## 다운로드

| 파일 | 실행 조건 |
|---|---|
| `SideBySide-v1.1.0-win-x64-selfcontained.zip` | 설치 없이 바로 실행 (권장) |
| `SideBySide-v1.1.0-win-x64.zip` | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치 필요, 용량 작음 |

압축을 풀고 `SideBySide.exe`를 실행하세요. Windows 10 1809 이상 / Windows 11 (x64).
서명되지 않은 실행 파일이라 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다 — **추가 정보 → 실행**.

## 사용법

- 창이 하나면 일반 탐색기, `Ctrl+D`로 두 창을 열면 비교
- 명령줄: `SideBySide.exe <왼쪽> <오른쪽>` — 폴더면 폴더 비교, 파일이면 파일 비교
- 단축키와 기능 전체는 [README](https://github.com/sangil1018/sidebyside#readme) 참고

설정: `%AppData%\SideBySide\settings.json` · 오류 로그: `%LocalAppData%\SideBySide\error.log`
