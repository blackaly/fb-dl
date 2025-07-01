# 📽️ fb-dl — Facebook Video Downloader (.NET CLI Tool)

**fb-dl** is a lightweight and efficient command-line tool built with .NET 9.0 that allows you to download public Facebook videos and reels by simply passing their URL. This tool is designed for developers, automation tasks, and anyone who wants a reliable way to retrieve Facebook-hosted video content.

---

## ✨ Features

- Download public Facebook videos by URL
- Built with modern .NET SDK 
- Fast and minimal 
- Easy to run from terminal or integrate into scripts 
- Ready for NuGet packaging and GitHub integration 

---

## 📥 Installation & Setup

### Prerequisites

Make sure the following are installed:

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet)
- Git

### 📦 Clone the Repository

```bash
git clone https://github.com/blackaly/fb-dl.git
cd fb-dl
```

### 🛠️ Build the Project

```bash
dotnet build -c Release
```

> This will compile the application into the `bin/Release/` directory.

---

## Usage

To download a video, run the tool from the CLI:

```bash
dotnet run --project src/facebook-downloader/facebook-downloader.csproj -- https://www.facebook.com/watch/?v=YOUR_VIDEO_ID
```

Replace the URL with a **public Facebook video** link.

---

## Example

```bash
dotnet run --project src/facebook-downloader/facebook-downloader.csproj -- https://www.facebook.com/watch/?v=123456789012345
```

After running, the video will be downloaded to your working directory.

---

## Create and Publish NuGet Package

### ack the Project

```bash
dotnet pack -c Release
```

> ⚠️ If you see `error NU5026` about missing `.runtimeconfig.json`, ensure the project is built first.

### 🚀 Publish to NuGet

1. Add NuGet source (only once):

   ```bash
   dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org
   ```

2. Push the package:

   ```bash
   dotnet nuget push bin/Release/facebook-downloader.*.nupkg --source nuget.org --api-key YOUR_API_KEY
   ```

---

## GitHub Integration for NuGet

Add the following to your `.csproj` to associate the package with this GitHub repository:

```xml
<PackageProjectUrl>https://github.com/blackaly/fb-dl</PackageProjectUrl>
<RepositoryUrl>https://github.com/blackaly/fb-dl</RepositoryUrl>
<RepositoryType>git</RepositoryType>
```

---

## Contributing

Contributions, bug reports, and feature requests are welcome!

### How to Contribute

1. Fork the repository
2. Create a new branch (`git checkout -b feature/your-feature`)
3. Make your changes
4. Commit your changes (`git commit -m "Add your message here"`)
5. Push to your fork (`git push origin feature/your-feature`)
6. Create a Pull Request

### Code Guidelines

- Follow .NET naming conventions
- Keep commits clear and concise
- Test your code before submitting PRs

---

## License

This project is licensed under the [MIT License](LICENSE).

---

## Contact

For support or questions, feel free to open an issue or contact the maintainer via [GitHub Issues](https://github.com/blackaly/fb-dl/issues).
