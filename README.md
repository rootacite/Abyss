<div align="center">

# Abyss (Server for Aether)

[![Plugin Version](https://img.shields.io/badge/Alpha-v0.1-red.svg?style=for-the-badge&color=76bad9)](https://github.com/rootacite/Abyss)

🚀This is the server of the multimedia application Aether, which can also be extended to other purposes🚀

<img src="abyss_clip.png" width="25%" alt="Logo">

</div>


---

## Description

**Abyss** is a modern, self-hosted media server and secure proxy platform built with **.NET 9**. It is designed to provide a highly secure, extensible, and efficient solution for managing and streaming media content (images, videos, live streams) while enforcing fine-grained access control and cryptographic authentication.

### 🎯 Key Features

- **Media Management**: Organize and serve images, videos, and live streams with structured directory support.
- **User Authentication**: Challenge-response authentication using **Ed25519** signatures. No private keys are ever transmitted.
- **Role-Based Access Control (RBAC)**: Hierarchical user privileges with configurable permissions for resources.
- **Secure Proxy**: Built-in HTTP/S proxy with end-to-end encrypted tunneling using **X25519** key exchange and **ChaCha20-Poly1305** encryption.
- **Resource-Level Permissions**: Fine-grained control over files and directories using a SQLite-based attribute system.
- **Task System**: Support for background tasks such as media uploads and processing with chunk-based integrity validation.
- **RESTful API**: Fully documented API endpoints for media access, user management, and task control.

### 🛠️ Technology Stack

- **Backend**: ASP.NET Core 9, MVC, Dependency Injection
- **Database**: SQLite with async ORM support
- **Cryptography**: NSec.Cryptography (Ed25519, X25519), ChaCha20Poly1305
- **Media Handling**: Range requests, MIME type detection, chunked uploads
- **Security**: Rate limiting, IP binding, token expiration, secure headers

### 🔐 Security Highlights

- Zero-trust architecture: All requests require valid tokens bound to IP addresses.
- No plaintext private key transmission.
- All media and metadata access is validated against a permission database.
- Secure tunneling with forward secrecy via ephemeral key exchange.

### 📦 Use Cases

- Personal media library with access control
- Secure internal video streaming platform
- Proxy server with authenticated tunneling
- Task-driven media processing pipeline

### 🌱 Extensibility

Abyss is designed with modularity in mind. Its service-based architecture allows easy integration of new media types, authentication providers, or storage backends.

---

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
4. Setup super user
5. Setup Media Library. **MEDIA_ROOT** environment variable specifies the root directory of the media library.But at this point, no files have been included in the management, so you cannot access any files through the API interface.

## API Quick Guide

This API provides a suite of user management and authentication services. All endpoints are rate-limited to prevent abuse. The authentication flow is based on a **challenge-response mechanism** using public-key cryptography.

---

**🔒 Authentication Flow**

The authentication process involves a three-step **challenge-response** flow:

1.  **Request a Challenge:** The client requests a challenge string for a specific user.
2.  **Sign the Challenge:** The client signs the challenge string using the user's private key.
3.  **Verify the Response:** The client sends the signed response back to the API for verification, receiving a session token upon success.

---

### 1. Request a Challenge

-   **Endpoint:** `GET /api/User/{user}`
-   **Description:** Requests a random challenge string for the specified user. This string must be signed and returned to complete the authentication. The challenge is valid for 1 minute.
-   **Parameters:**
    -   `user` (string, path): The username to get a challenge for.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** A Base64-encoded challenge string.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "Access forbidden"}` (e.g., if the user doesn't exist)

### 2. Verify a Challenge

-   **Endpoint:** `POST /api/User/{user}`
-   **Description:** Verifies the signed response to a previously issued challenge. A successful verification returns a session token valid for 1 day.
-   **Parameters:**
    -   `user` (string, path): The username.
-   **Body:**
    -   **Type:** `application/json`
    -   **Schema:** `{"response": "string"}`
        -   `response` (string, body): The Base64-encoded signature of the challenge string, created with the user's private key.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** A Base64-encoded session token.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "Access forbidden"}` (e.g., challenge expired or signature invalid)

### 3. Validate a Token

-   **Endpoint:** `POST /api/User/validate`
-   **Description:** Validates a session token. This endpoint verifies that the token is active and being used from the same IP address that obtained it.
-   **Parameters:**
    -   `token` (string, query): The session token to validate.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** The username associated with the token.
-   **Error Response:**
    -   **Code:** `401 Unauthorized`
    -   **Content:** `{"message": "Invalid"}` (e.g., token expired or from a different IP)

### 4. Destroy a Token

-   **Endpoint:** `POST /api/User/destroy`
-   **Description:** Invalidates a session token, immediately terminating the session.
-   **Parameters:**
    -   `token` (string, query): The session token to destroy.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** `Success`
-   **Error Response:**
    -   **Code:** `401 Unauthorized`
    -   **Content:** `{"message": "Invalid"}` (e.g., token is not valid)

### 5. Create a User

-   **Endpoint:** `PATCH /api/User/{user}`
-   **Description:** Creates a new user. This action requires a valid session token from the user's parent (or a user with higher privilege).
-   **Parameters:**
    -   `user` (string, path): The username of the new user to be created.
-   **Body:**
    -   **Type:** `application/json`
    -   **Schema:** `{"response": "string", "name": "string", "parent": "string", "privilege": "integer", "publicKey": "string"}`
        -   `response` (string, body): A signed response to a challenge, verifying the parent user's identity.
        -   `name` (string, body): The new user's unique username (alphanumeric only).
        -   `parent` (string, body): The username of the parent user creating this account.
        -   `privilege` (integer, body): The privilege level for the new user.
        -   `publicKey` (string, body): The new user's public key (Base64-encoded).
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** `Success`
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "Denied"}` (e.g., invalid token, user already exists, invalid username, or insufficient privilege)

**🎥 Video Endpoints**

These endpoints provide access to video resources. A valid token is required for all operations.

### 1. Initialize Resources

-   **Endpoint:** `POST /api/Video/init`
-   **Description:** Initializes the resource access control list for the video folder. This operation can only be performed by the **'root' user**.
-   **Parameters:**
    -   `token` (string, query): A valid session token for the `root` user.
    -   `owner` (string, query): The username to be set as the owner of the video resources.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** `true`
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., token is not from `root` user)

---

### 2. Get Video Classes

-   **Endpoint:** `GET /api/Video`
-   **Description:** Queries the top-level video directories (classes) available.
-   **Parameters:**
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** An array of strings representing the names of video classes.
-   **Error Response:**
    -   **Code:** `401 Unauthorized`
    -   **Content:** `{"message": "Unauthorized"}` (e.g., invalid token or insufficient permissions)

---

### 3. Query a Specific Class

-   **Endpoint:** `GET /api/Video/{klass}`
-   **Description:** Queries the contents of a specific video class directory.
-   **Parameters:**
    -   `klass` (string, path): The name of the video class.
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** An array of strings representing the items within the class directory.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., path traversal attempt)
    -   **Code:** `401 Unauthorized`
    -   **Content:** `{"message": "Unauthorized"}` (e.g., invalid token or insufficient permissions)

---

### 4. Query a Video Summary

-   **Endpoint:** `GET /api/Video/{klass}/{id}`
-   **Description:** Retrieves the summary information (as a JSON file) for a specific video.
-   **Parameters:**
    -   `klass` (string, path): The video class name.
    -   `id` (string, path): The video ID.
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** A JSON object containing video summary data.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., path traversal attempt or insufficient permissions)

---

### 5. Get Video Cover Image

-   **Endpoint:** `GET /api/Video/{klass}/{id}/cover`
-   **Description:** Serves the cover image for a video. Supports range processing for efficient streaming.
-   **Parameters:**
    -   `klass` (string, path): The video class name.
    -   `id` (string, path): The video ID.
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** The JPEG image file.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., path traversal attempt or insufficient permissions)

---

### 6. Get Gallery Image

-   **Endpoint:** `GET /api/Video/{klass}/{id}/gallery/{pic}`
-   **Description:** Serves an image from a video's gallery. Supports range processing.
-   **Parameters:**
    -   `klass` (string, path): The video class name.
    -   `id` (string, path): The video ID.
    -   `pic` (string, path): The name of the gallery image.
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** The JPEG image file.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., path traversal attempt or insufficient permissions)

---

### 7. Stream Video

-   **Endpoint:** `GET /api/Video/{klass}/{id}/av`
-   **Description:** Streams the video file. Supports range processing for seeking.
-   **Parameters:**
    -   `klass` (string, path): The video class name.
    -   `id` (string, path): The video ID.
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** The MP4 video file.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., path traversal attempt or insufficient permissions)

**🖼️ Image Endpoints**

These endpoints provide access to static image resources. A valid token is required for all operations.

---

### 1. Initialize Image Resources

-   **Endpoint:** `POST /api/Image/init`
-   **Description:** Initializes the resource access control list for the image folder. This operation can only be performed by the **'root' user**.
-   **Parameters:**
    -   `token` (string, query): A valid session token for the `root` user.
    -   `owner` (string, query): The username to be set as the owner of the image resources.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** `true`
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., token is not from `root` user)

---

### 2. Query Image Collections

-   **Endpoint:** `GET /api/Image`
-   **Description:** Queries the top-level image directories (collections) available.
-   **Parameters:**
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** An array of strings representing the names of image collections.
-   **Error Response:**
    -   **Code:** `401 Unauthorized`
    -   **Content:** `{"message": "Unauthorized"}` (e.g., invalid token or insufficient permissions)

---

### 3. Query a Specific Image's Summary

-   **Endpoint:** `GET /api/Image/{id}`
-   **Description:** Retrieves the summary information (as a JSON file) for a specific image.
-   **Parameters:**
    -   `id` (string, path): The ID of the image collection.
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** A JSON object containing the image summary data.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., path traversal attempt or insufficient permissions)

---

### 4. Get an Image File

-   **Endpoint:** `GET /api/Image/{id}/{file}`
-   **Description:** Serves a specific image file from a collection. Supports range processing for efficient streaming.
-   **Parameters:**
    -   `id` (string, path): The ID of the image collection.
    -   `file` (string, path): The name of the image file within the collection.
    -   `token` (string, query): A valid session token.
-   **Success Response:**
    -   **Code:** `200 OK`
    -   **Content:** The JPEG image file.
-   **Error Response:**
    -   **Code:** `403 Forbidden`
    -   **Content:** `{"message": "403 Denied"}` (e.g., path traversal attempt or insufficient permissions)

## TODO List

- [ ] Add P/D method to all controllers to achieve dynamic modification of media items
- [x] Implement identity management module
- [ ] Add a description of the media library directory structure in the READMD document
- [x] Add API interface instructions in the READMD document
