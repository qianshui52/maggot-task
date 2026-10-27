# 蛆虫任务 · Maggot Task

拖延的终点是苍蝇。

别的待办应用给你积分，给你小星星，给你一只会跳的猫。这个应用不哄人：它把你的每个任务变成一只活虫子放在桌面上，然后让它一直烂下去。

![Platform](https://img.shields.io/badge/platform-Android%20%7C%20Windows-brightgreen) ![Tests](https://img.shields.io/badge/tests-53%20%2B%20607-success) ![Network](https://img.shields.io/badge/network-none-black) ![License](https://img.shields.io/badge/license-MIT-blue)

## ⚡ 只想直接体验？

[**安装包文件夹**](以上为源代码，想直接体验请查看这个文件夹/)里放的都是能直接装、直接跑的东西。源码不动，还是在 `android/` 和 `windows/`。

| 平台 | 文件 | 前置条件 |
|:--|:--|:--|
| Windows 10/11 x64 | [`MaggotTask-Windows-1.3.2.zip`](以上为源代码，想直接体验请查看这个文件夹/MaggotTask-Windows-1.3.2.zip)（0.8 MB） | 需要 [.NET 7 桌面运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/7.0) |
| Android 8.0+ | [`MaggotTask-Android-1.2.10.apk`](以上为源代码，想直接体验请查看这个文件夹/MaggotTask-Android-1.2.10.apk)（7.0 MB） | 允许「未知来源」和悬浮窗权限 |

系统要求、安装步骤、两个版本的功能差异，都写在[那份文件夹的 README](以上为源代码，想直接体验请查看这个文件夹/README.md)里。元游戏目前只有 Windows 端有，Android 还是 1.2.10。

## 🥚 它是怎么转的

写下一个任务，按「产卵」，屏幕上就多一颗蛋。

```
🥚 卵 → 🐛 蛆 → 🛖 蛹 → 🪰 苍蝇      基准 90 分钟，速度 0.25× 到 8× 可调
```

- 点「我去做了」，虫子回血，还能接着当蛆
- 点「拍死它」，任务结束，进标本缸
- 什么都不做，它烂透、破壳，变成一只苍蝇趴在桌面上
- 继续不做，苍蝇开始繁殖，每一代都带着辈分（「记英语单词 · 第 7 代」）

没有关闭按钮，没有稍后提醒，也没有激励弹窗。你每次解锁屏幕都会看见它们，而它们正在烂。

它不锁手机，不拦操作，虫子只是在那儿爬。这就够了。

## ✨ 功能

| | |
|:--|:--|
| 🖥️ **全屏悬浮层** | 虫子画在真实桌面上。默认点击穿透，不挡任何操作；光标移到虫子上才临时接管 |
| 🧬 **会繁殖** | 烂透的任务孵出苍蝇，两只苍蝇随机配对生下一代，越拖越多 |
| 📅 **每日 / 每周任务** | 到点自动产卵。漏掉的那一周不会消失，会连本带利地烂 |
| 🖱️ **悬停查看** | 停 0.18 秒浮出名牌，停满 3 秒出详情气泡：哪个任务、烂到多少、多久没管 |
| 🎯 **亲手击毙** | 光标靠近就换成苍蝇拍，不用先去关穿透，点一下消除 |
| 💰 **击杀经济** | 卵 100、蛆 50、蛹 25、苍蝇 10。任务完成就结算，越早清越值钱 |
| 🔫 **军械库** | 喷雾 1,000 / 太刀 10,000 / 激光枪 50,000，范围越大冷却越长 |
| 🏅 **十枚勋章** | 从「第一拍」到「千虫之主」，全部由真实击杀和连胜算出来 |
| 🔥 **连胜日历** | 今天清一只就算打卡，配 5 周热力日历，断了会心疼 |
| 🔊 **音效** | 产卵、孵化、化蝇、交配、拍死，每个阶段都有声音，可关 |
| 🩺 **体检卡** | 虫群在不在、视口多大、时钟走没走，一张卡看完 |
| 💥 **崩溃记录** | 万一闪退，下次启动直接把堆栈摆出来，可一键复制 |
| ♿ **减少动效** | 系统开「减少动态效果」后虫子静止，但不会消失 |

## 📱 下载

### Android（8.0+）

从[安装包文件夹](以上为源代码，想直接体验请查看这个文件夹/)下 `MaggotTask-Android-1.2.10.apk`：

1. 允许安装未知来源应用
2. 首次打开按引导授予悬浮窗权限（SYSTEM_ALERT_WINDOW）
3. 点「让虫群上桌」

vivo / OPPO / 小米这类后台策略比较激进的 ROM，悬浮层上不了桌时应用会退回应用内显示，并在体检卡里写明原因。

### Windows（10/11）

下 `MaggotTask-Windows-1.3.2.zip`，**完整解压**后再双击 `MaggotTask.exe`：

1. 先装 [.NET 7 桌面运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/7.0)，或者 `winget install Microsoft.DotNet.DesktopRuntime.7`
2. 把 zip 解压到任意文件夹，别在压缩包里直接双击
3. 托盘出现虫子图标就算启动成功

没做代码签名，第一次运行 SmartScreen 会拦一下，点「更多信息」再点「仍要运行」即可。

## 🛠️ 从源码构建

Android（JDK 17、Android SDK 34、Gradle 8.7）：

```bash
cd android
./gradlew assembleRelease        # 产物在 app/build/outputs/apk/release/
./gradlew testDebugUnitTest      # 53 项单元测试（引擎 43 + 摆放回归 10）
```

Windows（.NET 7 SDK、WPF）：

```bash
cd windows
dotnet build MaggotTask.sln -c Release
./MaggotTask/bin/Release/net7.0-windows/MaggotTask.exe --selftest   # 607 项断言
```

奖励、价格、连胜、勋章条件这类数值写在 [`docs/meta-game.md`](docs/meta-game.md)，改之前先改那份。

## 🧠 两端为什么长得一样

先有规格，后有代码。[`docs/lifecycle-spec.md`](docs/lifecycle-spec.md)（15 节，含验收标准）是唯一事实来源。Android 的 `LifecycleEngine.kt` 和 Windows 的 `LifecycleEngine.cs` 各自照着它实现，腐烂数学、阶段机、繁殖、离线补偿、每日每周生成都对得上。要改机制就先改规格，两端跟着跑。

```
docs/lifecycle-spec.md   ←── 唯一事实来源（契约）
        │
   ┌────┴─────┐
android/    windows/
Kotlin      C# / WPF
Compose     悬浮穿透窗
```

`state.json` v4 两端共用，换设备不用重新养虫。

## 🔒 隐私

Android 的清单里没有申请 INTERNET 权限，虫子想上传也找不到门路。所有数据只存在本地（Android 私有目录，Windows 在 `%APPDATA%\MaggotTask`）。没有账号，没有云同步，没有埋点。

## ⚠️ 已知限制

- 国产 ROM 的省电策略可能杀后台服务，应用会降级到应用内显示并在体检卡注明，建议把电池策略设成「无限制」
- 部分机型会把 `MATCH_PARENT` 悬浮窗量成 0×0，已经改用显式像素尺寸规避
- Windows 端需要 .NET 7 桌面运行时

排障手册和开发笔记在 [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md)。

## 📜 灵感

写给每一个装过十七个番茄钟、最后全躺在文件夹里吃灰的人。这次不一样，这次它们会烂。

## 📄 许可证

MIT。拿去用，拿去改，拿去治你的拖延。只有一件事请留着：拍死音效，那是整个应用里唯一温柔的暴力。

---

**今天不管，明天桌面多一只。**

🥚 → 🐛 → 🛖 → 🪰 → 🪰🪰 → 🪰🪰🪰🪰……
