[English](README.md) | Tiếng Việt

# Bing Wallpaper Updater

Ứng dụng nhỏ chạy ở khay hệ thống, giữ cho màn hình nền Windows của bạn luôn là ảnh Bing mới nhất, lấy từ danh mục [niumoo/bing-wallpaper](https://github.com/niumoo/bing-wallpaper) - dưới 100 MB RAM, không cần quyền quản trị, không gửi dữ liệu đi đâu.

Ảnh chụp màn hình: sắp có.

## Ứng dụng làm gì

- Đổi hình nền theo lịch bạn chọn (30 phút một lần cho đến mỗi ngày một lần) sang ảnh Bing mới nhất, hoặc ngẫu nhiên một ảnh trong bộ nhớ đệm nhỏ trên máy.
- **Hình nền tiếp theo** trong menu khay hoặc cửa sổ Cài đặt đổi ảnh ngay lập tức.
- Phủ một ảnh lên tất cả màn hình, hoặc mỗi màn hình một ảnh riêng.
- Giữ tối đa 10 ảnh trên đĩa và tự tải bù các ngày trước đó sau khi cài, để chế độ ngẫu nhiên có ảnh mà chọn.
- Có tiếng Anh và tiếng Việt (theo ngôn ngữ hiển thị của Windows, hoặc bạn tự chọn).
- Có thể khởi động cùng Windows (theo từng người dùng, hiện trong Task Manager > Startup apps, nơi bạn có thể tắt nó).
- Hiển thị tiêu đề và dòng bản quyền của ảnh hiện tại.

Những điều ứng dụng không bao giờ làm:

- không bao giờ tự mở trình duyệt hay bất kỳ cửa sổ nào (chỉ có biểu tượng ở khay cho đến khi bạn mở Cài đặt);
- không bao giờ "gọi về nhà" - không có telemetry, không báo cáo lỗi, không kiểm tra cập nhật;
- không bao giờ cần quyền quản trị, dù là cài, chạy hay gỡ;
- không dùng, không cài và không phụ thuộc vào ứng dụng Bing Wallpaper của Microsoft - nó thay thế ứng dụng đó.

## Cài đặt

1. Tải `BingWallpaperUpdater-<version>-x64-Setup.exe` từ [trang Releases](https://github.com/anhyeuviolet/BingWallpaperUpdater/releases). Chỉ tải từ đó.
2. Chạy tệp. Windows SmartScreen sẽ báo "Windows protected your PC" vì bộ cài không được ký số (chứng chỉ ký mã tốn tiền, dự án này không chi khoản đó). Bấm **More info**, rồi **Run anyway**.
3. **Đừng chạy bộ cài với quyền quản trị** (không chuột phải > Run as administrator, không dùng terminal đã nâng quyền). Ứng dụng cài theo từng người dùng; bộ cài chạy với quyền quản trị sẽ cài vào hồ sơ của tài khoản quản trị thay vì của bạn, và không thể mở ứng dụng khi cài xong. Nếu chuyện đó xảy ra, bộ cài sẽ cảnh báo và chọn sẵn Cancel.
4. Chọn ngôn ngữ bộ cài (English hoặc Tiếng Việt). Không có trang chọn thư mục: ứng dụng luôn nằm ở `%LocalAppData%\Programs\BingWallpaperUpdater\`.
5. Giữ dấu tick **Khởi động cùng Windows** nếu bạn muốn hình nền tiếp tục đổi sau khi khởi động lại máy. Có thể đổi lại trong Cài đặt bất cứ lúc nào.
6. Hoàn tất. Ứng dụng khởi chạy, tải ảnh Bing mới nhất và đặt làm hình nền; biểu tượng xuất hiện ở khay cạnh đồng hồ.

Bộ cài nặng khoảng 34 MB vì nó gói sẵn .NET 10 runtime (self-contained). Không phải cài thêm gì và không có hộp thoại UAC.

### Kiểm tra tệp đã tải

Mỗi bản phát hành có một tệp `.sha256` kèm theo bộ cài. So sánh nó với hash của tệp bạn tải về:

```powershell
Get-FileHash .\BingWallpaperUpdater-<version>-x64-Setup.exe -Algorithm SHA256
```

hoặc trên máy không có `Get-FileHash`:

```cmd
certutil -hashfile BingWallpaperUpdater-<version>-x64-Setup.exe SHA256
```

Chuỗi hex phải trùng với trường đầu tiên trong `BingWallpaperUpdater-<version>-x64-Setup.exe.sha256`. Bộ cài chỉ được GitHub Actions dựng từ thẻ phiên bản; mỗi bản phát hành có liên kết tới lần chạy workflow tương ứng.

Chứng thực nguồn gốc bản dựng (`gh attestation verify BingWallpaperUpdater-<version>-x64-Setup.exe -R anhyeuviolet/BingWallpaperUpdater`) chỉ được tạo cho các thẻ dựng khi kho mã đang công khai. Kho mã còn ở chế độ riêng tư khi bản phát hành đầu tiên được dựng, nên bản đó không có chứng thực; các bản sau sẽ có khi kho mã đã công khai.

## Ứng dụng đụng vào những gì

Tất cả đều theo từng người dùng. Không có gì dưới `HKLM`, `Program Files` hay Task Scheduler.

| Ở đâu | Cái gì |
|-------|--------|
| `%LocalAppData%\Programs\BingWallpaperUpdater\` | Ứng dụng và .NET runtime đi kèm (khoảng 220 tệp), cùng `unins000.exe` / `unins000.dat` (trình gỡ cài đặt). |
| `%LocalAppData%\BingWallpaperUpdater\` | Thư mục dữ liệu: `settings.json` (cài đặt của bạn), `state.json` (lịch và ảnh hiện tại), `log.txt` (nhật ký dạng văn bản, xoay vòng khi đạt 256 KB), `cache\index.json` (thông tin ảnh), `cache\catalog.md` (trang danh mục gần nhất), `cache\*.jpg` (tối đa 10 ảnh). |
| `%AppData%\Microsoft\Windows\Start Menu\Programs\Bing Wallpaper Updater.lnk` | Lối tắt trong Start menu. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4AD7C4B2-6A1C-443D-95F9-74E5239D058C}_is1` | Mục gỡ cài đặt hiện trong Settings > Apps. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\BingWallpaperUpdater` | `"<thư mục cài>\BingWallpaperUpdater.exe" --startup` - tồn tại khi Khởi động cùng Windows đang bật. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\BingWallpaperUpdater` | Cờ bật/tắt của chính Windows cho mục Run đó. Chỉ được ghi sau khi bạn bật/tắt Khởi động cùng Windows trong Cài đặt (bộ cài không bao giờ ghi giá trị này). |
| `HKCU\Control Panel\Desktop` - `WallpaperStyle` = `10`, `TileWallpaper` = `0` | Chỉ được ghi khi giao diện COM `IDesktopWallpaper` không dùng được và ứng dụng phải chuyển sang `SystemParametersInfo`. Bản thân Windows cũng ghi đường dẫn hình nền vào đó (`Wallpaper`, `TranscodedWallpaper`) mỗi khi bất kỳ ứng dụng nào đặt hình nền. |

## Cài đặt trong ứng dụng

Mở cửa sổ Cài đặt từ biểu tượng ở khay (chuột phải > **Cài đặt**), hoặc đơn giản là chạy ứng dụng lần nữa từ Start menu: lần chạy thứ hai không tạo thêm tiến trình mới mà mở cửa sổ Cài đặt của tiến trình đang chạy.

| Tùy chọn | Giá trị |
|----------|---------|
| Kiểm tra mỗi | 30 phút, 1 giờ, 2 giờ, 4 giờ, 8 giờ, 24 giờ |
| Cách xoay ảnh | Mới nhất (luôn là ảnh Bing hiện tại) hoặc Ngẫu nhiên (một ảnh bất kỳ trong bộ nhớ đệm) |
| Độ phân giải | Tự động (theo màn hình lớn nhất), UHD (3840x2160), 1920x1200, 1920x1080 |
| Khu vực Bing | en-US (mặc định; khớp với danh mục), en-GB, en-AU, en-CA, en-IN, de-DE, fr-FR, ja-JP, zh-CN, vi-VN |
| Màn hình | Cùng một ảnh trên mọi màn hình, hoặc mỗi màn hình một ảnh |
| Khởi động cùng Windows | bật / tắt (ghi hoặc xóa giá trị Run trong HKCU nói trên) |
| Ngôn ngữ | Theo ngôn ngữ hiển thị của Windows, English, Tiếng Việt |

Khung **Ảnh hiện tại** hiển thị tiêu đề, bản quyền, ngày, lần kiểm tra cuối, lần kiểm tra tiếp theo và lỗi gần nhất (nếu có). **Hình nền tiếp theo** đổi ảnh ngay; **Mở thư mục bộ nhớ đệm** mở `%LocalAppData%\BingWallpaperUpdater\cache\` trong Explorer. Mọi thay đổi được lưu ngay khi bạn chọn.

## Gỡ cài đặt

Settings > Apps > Installed apps > **Bing Wallpaper Updater** > Uninstall (hoặc chạy trình gỡ trong thư mục cài).

- Ứng dụng đang chạy sẽ được yêu cầu thoát và tự đóng; bạn không cần thoát nó trước.
- Trình gỡ hỏi: *Xóa luôn ảnh nền đã tải và cài đặt trong %LocalAppData%\BingWallpaperUpdater?* **No** được chọn sẵn. Trả lời Yes nếu muốn xóa cả thư mục dữ liệu.
- Dù chọn gì, trình gỡ cũng xóa thư mục cài, lối tắt Start menu, mục gỡ cài đặt, giá trị Run và giá trị StartupApproved.
- Hình nền hiện tại vẫn được giữ cho đến khi bạn đổi.

## Bộ nhớ

Đo bằng [`tools/soak.ps1`](tools/soak.ps1) trên bản dựng đã publish, bắt đầu từ thư mục dữ liệu trống: 20 lần mở/đóng cửa sổ Cài đặt, rồi 200 lần đổi hình nền qua nút Hình nền tiếp theo, lấy mẫu mỗi 15 giây. Cả lượt đo mất 6,4 phút (đây là soak nén: rò rỉ bộ nhớ lộ ra theo số lần lặp, không phải theo số giờ). Tệp CSV và dòng kết luận được commit trong [`docs/soak/`](docs/soak/); dòng bảng bên dưới do lệnh `tools/soak.ps1 -Summarize docs/soak/win11-soak.csv` in ra - không có con số nào ở đây được gõ tay.

| OS | Max private WS (Task Manager "Memory") | Private WS, window closed, end of run | Max working set | Max commit | Private WS slope | Run | Evidence |
|----|----------------------------------------|---------------------------------------|-----------------|------------|------------------|-----|----------|
| Microsoft Windows 11 IoT Enterprise LTSC 10.0.26100 | 30.6 MB | 26.1 MB | 106.5 MB | 36.4 MB | -6.72 KB/tick | compressed soak, 200 ticks + 20 Settings cycles, ~7 min | win11-soak.csv |
| Windows 10 LTSC/Enterprise 1809 | not yet measured | | | | | | |

"Private WS" là con số cột Memory trong Task Manager và là điều mà lời hứa dưới 100 MB nói tới: các trang bộ nhớ chỉ tiến trình này dùng. Working set tổng lớn hơn vì gồm khoảng 50 MB trang .NET runtime dùng chung, ánh xạ từ tệp, mà mọi tiến trình .NET đều nạp và Windows chỉ đếm một lần. Đỉnh (30.6 MB) là lúc cửa sổ Cài đặt đang mở; khi đóng cửa sổ, ứng dụng ở mức 19-26 MB.

Lỗi đã biết: lượt đo kết thúc với `SOAK WARN reason=gdi growth`: mỗi lần mở/đóng cửa sổ Cài đặt thêm khoảng 7 đối tượng GDI (14 -> 160 sau 20 lần; giữ nguyên suốt 200 lần đổi ảnh); private working set, số handle và đối tượng USER không tăng. Vấn đề này đang được theo dõi để sửa trong đợt vá Phase 4 (giới hạn GDI mỗi tiến trình là 10.000).

Bằng chứng: [`docs/soak/win11-soak.csv`](docs/soak/win11-soak.csv) và [`docs/soak/win11-soak.csv.result.txt`](docs/soak/win11-soak.csv.result.txt). Dòng Windows 10 1809 sẽ được điền theo đúng cách này khi có máy để đo.

## Các phiên bản Windows được hỗ trợ

| Windows | Hỗ trợ |
|---------|--------|
| Windows 11 (x64) | Hỗ trợ, đã đo |
| Windows 10 LTSC / Enterprise 1809 trở lên (x64) | Hỗ trợ (theo danh sách hỗ trợ của .NET 10 runtime); chưa đo |
| Các bản Windows 10 khác (x64) | Cố gắng hết sức - Windows 10 bản người dùng phổ thông đã hết vòng đời, runtime không còn được kiểm thử trên đó |

Bộ cài từ chối chạy dưới Windows 10 build 17763 (phiên bản 1809). Chỉ dựng bản x64; Windows trên Arm64 chạy qua giả lập và chưa được thử.

## Mạng

Ứng dụng chỉ nói chuyện với đúng ba máy chủ, tất cả qua HTTPS, và không gì khác:

| Máy chủ | Mục đích |
|---------|----------|
| `raw.githubusercontent.com` | Danh mục niumoo/bing-wallpaper (README và các trang theo tháng), lấy bằng conditional GET (`If-None-Match`, nên trang không đổi trả về 304 không có nội dung) |
| `www.bing.com` | API kho ảnh Bing (tiêu đề và bản quyền) và các tệp ảnh |
| `cn.bing.com` | Máy chủ dự phòng cho tệp ảnh, dùng khi `www.bing.com` lỗi |

Mỗi lần kiểm tra theo lịch, ứng dụng tải nhiều nhất một ảnh mới, cộng thêm một ảnh cũ hơn (tải bù) khi bộ nhớ đệm chưa đầy. Không có telemetry, không kiểm tra cập nhật, không máy chủ nào khác - danh sách máy chủ cho phép được kiểm tra ngay trong mã, và yêu cầu tới bất kỳ địa chỉ nào khác bị từ chối trước khi gửi đi. `User-Agent` là `BingWallpaperUpdater/<version> (+https://github.com/anhyeuviolet/BingWallpaperUpdater)`.

## Câu hỏi thường gặp

**Hình nền cứ tự đổi về ảnh khác.** Windows Spotlight hoặc đồng bộ cài đặt của Windows đang đặt lại hình nền của nó. Tắt Spotlight (Settings > Personalization > Background > Personalize your background: Picture) hoặc loại hình nền khỏi đồng bộ (Settings > Accounts > Windows backup > Remember my preferences > Personalization).

**Hình nền trông bị nén hơn tệp đã tải.** Windows nén lại mọi hình nền sang JPEG với chất lượng riêng của nó khi áp dụng. Giá trị registry `JPEGImportQuality` trong `HKCU\Control Panel\Desktop` điều khiển chất lượng đó; ứng dụng này không đặt giá trị ấy, và đây chỉ là thông tin tham khảo - tự chỉnh thì tự chịu trách nhiệm.

**Tôi không thấy biểu tượng ở khay trên Windows 11.** Windows 11 giấu biểu tượng khay mới vào vùng tràn (dấu `^` cạnh đồng hồ). Mở vùng tràn và kéo biểu tượng ra thanh tác vụ, hoặc bật nó trong Settings > Personalization > Taskbar > Other system tray icons.

**Chạy bộ cài với quyền quản trị để cài cho mọi người dùng được không?** Không. Ứng dụng được thiết kế theo từng người dùng; bộ cài chạy với quyền quản trị sẽ cài vào hồ sơ của tài khoản quản trị và không mở được ứng dụng. Đừng chạy bộ cài với quyền quản trị; mỗi người dùng tự cài một lần.

**Vì sao bộ nhớ đệm đầy dần trong vài ngày đầu, và khi đầy thì ảnh nào bị xóa trước?** Sau khi cài, ứng dụng chỉ có ảnh của hôm nay. Mỗi lần kiểm tra theo lịch, nó tải thêm một ngày cũ hơn từ danh mục (tải bù) cho đến khi bộ nhớ đệm có 10 ảnh, nên chế độ Ngẫu nhiên có lựa chọn thật sự chỉ sau vài lần kiểm tra. Khi đã đủ 10 ảnh, ảnh được tải sớm nhất bị xóa trước (`DownloadedUtc` nhỏ nhất mà không phải ảnh đang dùng), nên những ngày cũ được tải bù sau khi cài sẽ bị xóa sau cùng - đây là hành vi v1 được chấp nhận. Với các khu vực khác en-US, danh mục là trang HPImageArchive 8 ngày của Bing, nên độ sâu tải bù ở đó là 8 thay vì 9.

**Vì sao bộ cài nặng khoảng 34 MB?** Nó chứa trọn bộ .NET 10 runtime, nên không phải cài gì ở mức hệ thống và không bao giờ có hộp thoại UAC. Bản phụ thuộc framework chỉ vài MB nhưng sẽ cần cài .NET Desktop Runtime cho từng máy, với quyền nâng cao.

**Vì sao SmartScreen cảnh báo?** Bộ cài không được ký bằng chứng chỉ ký mã. Hãy kiểm tra SHA-256 như hướng dẫn ở trên, rồi bấm More info > Run anyway.

## Dựng từ mã nguồn

- .NET 10 SDK (phiên bản chính xác được ghim trong `global.json`; `dotnet --version` phải in ra 10.0.4xx).
- Kiểm thử: `dotnet test tests/BingWallpaperUpdater.Core.Tests -c Release`
- Bộ cài: `powershell -File tools/build-installer.ps1 -Version 0.0.0` (cần Inno Setup 6.7.x; script publish ứng dụng dạng self-contained và biên dịch `installer/setup.iss` vào `dist/`).
- Các công cụ kiểm tra dùng trong quá trình phát triển, đều là PowerShell 5.1: `tools/install-probe.ps1` (vòng cài / gỡ im lặng), `tools/smoke-verify.ps1` (kiểm tra nhanh qua nhật ký của tiến trình đang chạy), `tools/soak.ps1` (đo bộ nhớ như trên), `tools/settings-size-probe.ps1`, `tools/autostart-probe.ps1`.

## Quy trình phát hành

1. Đẩy thẻ `vX.Y.Z` trên `master` (phải trùng `<Version>` trong `Directory.Build.props`).
2. GitHub Actions (`.github/workflows/release.yml`, trên runner `windows-2025` được ghim) chạy kiểm thử, publish ứng dụng, biên dịch bộ cài bằng Inno Setup 6.7.1, ghi `BingWallpaperUpdater-X.Y.Z-x64-Setup.exe.sha256`, kiểm tra lại bằng `sha256sum -c`, rồi tạo một bản phát hành **nháp** tên `BingWallpaperUpdater X.Y.Z` với hai tệp đó đính kèm.
3. Người bảo trì tự viết ghi chú phát hành theo `.github/RELEASE_NOTES_TEMPLATE.md` (điểm nổi bật, câu về SmartScreen, mã SHA-256, dòng bộ nhớ, Windows được hỗ trợ) rồi bấm Publish. Không có gì được công bố tự động.

## Ghi công

- [niumoo/bing-wallpaper](https://github.com/niumoo/bing-wallpaper) - danh mục ID ảnh Bing hằng ngày, nhờ đó mới có tính năng tải bù và lịch sử ảnh.
- Ảnh là của Bing; bản quyền thuộc Microsoft và các nhiếp ảnh gia. Ứng dụng hiển thị dòng bản quyền của mọi ảnh nó đặt.
- [Inno Setup](https://jrsoftware.org/isinfo.php) - bộ cài (miễn phí cho mục đích phi thương mại).
- [.NET](https://dotnet.microsoft.com/) và Windows Forms.

## Giấy phép

MIT - xem [LICENSE](LICENSE). Copyright (c) 2026 Kenny Nguyen (anhyeuviolet).
