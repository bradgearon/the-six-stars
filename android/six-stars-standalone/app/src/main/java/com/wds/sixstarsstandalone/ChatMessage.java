package com.wds.sixstarsstandalone;

final class ChatMessage {
    final String role;
    final String content;
    ChatMessage(String role, String content) { this.role = role; this.content = content == null ? "" : content; }
}
