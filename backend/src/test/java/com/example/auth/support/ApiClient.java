package com.example.auth.support;

import jakarta.servlet.http.Cookie;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;
import org.springframework.test.web.servlet.request.MockHttpServletRequestBuilder;

import java.util.UUID;

import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;

/** Thin helper that drives the public auth API the way the browser does. */
public class ApiClient {

    public static final String PASSWORD = "Correct-Horse-Battery-9";
    public static final String COOKIE = "refresh_token";

    private final MockMvc mvc;

    public ApiClient(MockMvc mvc) {
        this.mvc = mvc;
    }

    public static String uniqueEmail() {
        return "user-" + UUID.randomUUID() + "@example.com";
    }

    public static String json(String... kv) {
        StringBuilder sb = new StringBuilder("{");
        for (int i = 0; i < kv.length; i += 2) {
            if (i > 0) sb.append(',');
            sb.append('"').append(kv[i]).append("\":\"").append(kv[i + 1]).append('"');
        }
        return sb.append('}').toString();
    }

    public MvcResult register(String email, String password) throws Exception {
        return mvc.perform(post("/api/auth/register").contentType(MediaType.APPLICATION_JSON)
                .content(json("email", email, "password", password, "firstName", "Test", "lastName", "User")))
                .andReturn();
    }

    public MvcResult login(String email, String password) throws Exception {
        return mvc.perform(post("/api/auth/login").contentType(MediaType.APPLICATION_JSON)
                .content(json("email", email, "password", password))).andReturn();
    }

    public MvcResult refresh(String refreshToken) throws Exception {
        return mvc.perform(withCookie(post("/api/auth/refresh"), refreshToken)).andReturn();
    }

    public MvcResult logout(String refreshToken) throws Exception {
        return mvc.perform(withCookie(post("/api/auth/logout"), refreshToken)).andReturn();
    }

    public static String refreshCookie(MvcResult result) {
        Cookie c = result.getResponse().getCookie(COOKIE);
        return c == null ? null : c.getValue();
    }

    public static String accessToken(MvcResult result) throws Exception {
        String body = result.getResponse().getContentAsString();
        return com.jayway.jsonpath.JsonPath.read(body, "$.accessToken");
    }

    private static MockHttpServletRequestBuilder withCookie(MockHttpServletRequestBuilder b, String token) {
        b.header("X-Requested-With", "XMLHttpRequest");
        if (token != null) {
            b.cookie(new Cookie(COOKIE, token));
        }
        return b;
    }
}
