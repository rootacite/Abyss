<div align="center">

# Abyss (Server for Aether)

[![Plugin Version](https://img.shields.io/badge/Alpha-v0.1-red.svg?style=for-the-badge&color=76bad9)](https://github.com/rootacite/Abyss)

_🚀This is the server of the multimedia application Aether, which can also be extended to other purposes🚀_

</div>

<br/>
<br/>
<br/>

## Development environment

- Operating System: Voidraw OS v1.1 (based on Ubuntu) or any compatible Linux distribution.
- .NET SDK: Version 9.0 or higher. You can download it from the official .NET website.

## Getting Started

1. Clone Repository

   ```bash
   git clone https://github.com/rootacite/Abyss
   ```
2. Setup environment variables (Based on your actual situation)

   ```bash
   export ASPNETCORE_URLS="https://0.0.0.0:443;http://0.0.0.0:80"
   export MEDIA_ROOT="/opt"
   ```
3. Run

   ```bash
   cd ./Abyss
   dotnet restore
   dotnet run
   ```

## TODO List

- [ ] Add P/D method to all controllers to achieve dynamic modification of media items
- [ ] Implement identity management module
- [ ] Add a description of the media library directory structure in the READMD document
- [ ] Add API interface instructions in the READMD document
