# 貢獻指南

感謝你協助改善 Mabi Life｜瑪奇生活助手。

## 開發環境

- Windows
- .NET 8 SDK
- Git

專案使用 `global.json` 固定 .NET 8 SDK feature band。若本機沒有 SDK，請安裝 .NET 8 SDK；已發布的 Windows 單檔版本不需要額外安裝 .NET。

## 本機檢查

```powershell
dotnet restore .\MabiLifeAssistant.sln
dotnet build .\MabiLifeAssistant.sln -c Release
dotnet test .\tests\MabiLifeAssistant.Tests\MabiLifeAssistant.Tests.csproj -c Release
```

## 影像資料與模型

請不要提交包含個人視窗、帳號名稱或遊戲畫面的資料。訓練圖片、診斷截圖與發布輸出放在 `artifacts/`、`dist*/`，這些路徑已列入 `.gitignore`。

若要調整工作狀態模型，請在變更說明中記錄來源場景、解析度、資料分割方式，以及獨立測試結果。由同一張來源截圖產生的增強圖片只能作為開發驗證，不能當成獨立泛化結果。

## Pull request

- 說明變更目的與使用者可見的影響。
- 附上 `dotnet build` 與 `dotnet test` 結果。
- 影像辨識調整請附上誤判案例與安全停止行為。
- 保留 `LICENSE` 與 `NOTICE` 的作者標示。
