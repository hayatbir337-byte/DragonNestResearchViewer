# Dragon Nest Research Viewer V1

Windows için bağımsız Dragon Nest / Eternity Engine resource görüntüleyici.

## V1
- Resource klasörü seçme
- .skn / .msh / .dds / .ani / .anim dosyalarını tarama
- SKN -> bağlı MSH + DDS çözümleme
- MSH mesh / UV / rig / bone okuma
- DDS texture görüntüleme
- ANI/ANIM animation listesi
- Play / Pause / Stop / Loop / timeline
- Skeleton aç/kapat
- 3D rotate / pan / zoom

## Build
Windows 10/11 ve .NET 8 SDK:

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project DragonNestResearchViewer.csproj
```

GitHub Actions her main pushunda win-x64 build üretir.

> V1 read-only çalışır. Kaynak Dragon Nest dosyalarına yazmaz.
