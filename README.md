# SPWare Wind Sound

**English** | [한국어](#한국어)

A small Windows (WPF, .NET 8) app that plays MIDI from games and sequencers through two software sound-module emulators:

| Name in the app | What it is | Based on |
|---|---|---|
| **SPE3** | MT-32 compatible sound emulator | [Munt](https://github.com/munt/munt) (mt32emu) |
| **SPE5** | SC-55mkII compatible sound emulator | [Nuked-SC55](https://github.com/nukeykt/Nuked-SC55) |

Incoming MIDI arrives through a virtual MIDI port (e.g. [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html)), is routed per channel to one of the engines, and is played through WASAPI. The window shows the front panel of each engine (LCD, buttons, volume knob).

> **No ROMs are included and none may be committed to this repository.** The emulators need ROM data dumped from hardware you own. See `Roms/ROMS_README.txt` in the release package for the folder and file names.

## Download

Get the ready-to-run package from the **Releases** page (`SPWare.WindSound_public.zip`). No .NET installation is needed.

## Build from source

Requirements: Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet publish SPWare.VirtualSoundCanvas.csproj -c Release -r win-x64
```

The output is a single `SPWare.WindSound.exe` in `bin/Release/net8.0-windows/win-x64/publish/`.
The prebuilt native libraries (`Native/x64/mt32_wrap.dll`, `nuked_sc55_wrap.dll`) are built from the sources in `native-src/` (see below).

## Native libraries and licenses

* `Native/x64/mt32_wrap.dll` – mt32emu (Munt, **LGPL-2.1-or-later**) plus a thin C wrapper. Source: `native-src/munt-mt32emu-wrap-source.zip`.
* `Native/x64/nuked_sc55_wrap.dll` – modified Nuked-SC55 (**original MAME license: no selling, no commercial use; modified versions must ship their complete source**). Source: `native-src/Nuked-SC55-wrap-source.zip`.
* NAudio (MIT).

This project's own code is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)** (free for noncommercial use; no selling, no commercial use). This also matches the Nuked-SC55 condition below. Third-party components keep their own licenses (texts in `Licenses/` and inside the source zips).

Roland and its product names are trademarks of Roland Corporation. This project is not affiliated with, sponsored by or endorsed by Roland.

## Language

The UI is available in English and Korean (Settings → Language). *Auto* follows your Windows language.

## Changes

* **1.0.3** – Fixed an empty/wrong sound after you manually switch the MIDI type or default engine while a game is running (for example a game that uploads its own instruments after selecting a program). The song setup is now re-sent to the new engine in the same order it originally arrived.
* **1.0.2** – Fixed the "missing sounds" when you change the MIDI type or the default engine (SPE3 ↔ SPE5) while a song is playing: the new engine now receives the song's current setup (instrument, volume, pan, effects). Auto-detected switches at the start of a song are unchanged.
* **1.0.1** – Fixed a crash that could happen when closing the program (use-after-free in the MT-32 wrapper's `mt32_destroy`). Settings and the SC-55 backup are now saved before shutdown even if the framework fails while closing.
* **1.0.0** – First release.

---

## 한국어

게임/시퀀서의 MIDI를 두 가지 소프트웨어 음원 에뮬레이터로 재생하는 Windows(WPF, .NET 8) 프로그램입니다.

* **SPE3** – MT-32 호환 음원 에뮬레이터 ([Munt](https://github.com/munt/munt) 기반)
* **SPE5** – SC-55mkII 호환 음원 에뮬레이터 ([Nuked-SC55](https://github.com/nukeykt/Nuked-SC55) 기반)

가상 MIDI 포트(loopMIDI 등)로 들어온 MIDI를 채널별로 엔진에 나눠 보내고 WASAPI로 재생합니다. 각 엔진의 앞판(LCD, 버튼, 볼륨 노브)이 화면에 나옵니다.

> **ROM은 포함되어 있지 않으며, 이 저장소에 올려서도 안 됩니다.** 본인이 가진 기기에서 직접 준비하세요. 폴더/파일 이름은 릴리스 패키지의 `Roms/ROMS_README.txt`에 있습니다.

* **내려받기**: Releases 페이지의 `SPWare.WindSound_public.zip` (.NET 설치 불필요)
* **빌드**: .NET 8 SDK 설치 후 `dotnet publish SPWare.VirtualSoundCanvas.csproj -c Release -r win-x64`
* **라이선스**: 이 프로젝트의 코드는 **PolyForm Noncommercial 1.0.0**([LICENSE](LICENSE))입니다 - 비상업적 사용만 가능하고 판매/상업적 사용은 안 됩니다(Nuked-SC55의 MAME 라이선스 조건과도 같은 방향). Munt는 LGPL-2.1 이상이며, 외부 구성요소는 각자의 라이선스를 따릅니다. 네이티브 DLL의 소스는 `native-src/`에 있습니다.
* Roland 및 제품명은 Roland Corporation의 상표이며, 이 프로젝트는 Roland와 제휴/후원/승인 관계가 없습니다.
* 화면 언어는 설정 → Language / 언어 에서 바꿀 수 있습니다(Auto = 윈도 언어).

Copyright (c) SPWare (Special Wind Software)

## 변경 사항

* **1.0.3** – 게임을 실행하는 도중 MIDI 종류나 기본 엔진을 손으로 바꾸면 소리가 비거나 다르게 나던 문제를 고쳤습니다(음색 선택 뒤에 게임이 자기 음색을 올리는 방식 등). 곡의 설정을 새 엔진에 원래 도착한 순서 그대로 다시 보냅니다.
* **1.0.2** – 곡을 재생하는 도중에 MIDI 종류나 기본 엔진(SPE3 ↔ SPE5)을 바꾸면 소리가 빠지던 문제를 고쳤습니다. 새 엔진이 곡의 현재 설정(음색, 음량, 팬, 이펙트)을 이어받습니다. 곡이 시작될 때 자동으로 바뀌는 경우는 그대로입니다.
* **1.0.1** – 프로그램을 끌 때 비정상 종료될 수 있던 문제를 고쳤습니다(MT-32 래퍼 `mt32_destroy`의 해제 후 사용). 프레임워크가 종료 중 오류를 내도 설정과 SC-55 백업이 먼저 저장됩니다.
* **1.0.0** – 첫 릴리스.
