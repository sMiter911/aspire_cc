package com.example.auth.auth;

import com.example.auth.auth.entity.RefreshToken;
import com.example.auth.auth.repository.RefreshTokenRepository;
import com.example.auth.support.ApiClient;
import com.example.auth.support.IntegrationTest;
import com.example.auth.user.entity.User;
import com.example.auth.user.repository.UserRepository;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;

import static com.example.auth.support.ApiClient.PASSWORD;
import static com.example.auth.support.ApiClient.accessToken;
import static com.example.auth.support.ApiClient.refreshCookie;
import static com.example.auth.support.ApiClient.uniqueEmail;
import static org.assertj.core.api.Assertions.assertThat;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

@IntegrationTest
class AuthFlowIT {

    @Autowired MockMvc mvc;
    @Autowired UserRepository users;
    @Autowired RefreshTokenRepository refreshTokens;

    ApiClient api;

    @BeforeEach
    void setUp() {
        api = new ApiClient(mvc);
    }

    // ---- registration -----------------------------------------------------------------------------

    @Test
    void registerSucceedsAndNeverReturnsSecrets() throws Exception {
        String email = uniqueEmail();
        MvcResult result = api.register("  " + email.toUpperCase() + " ", PASSWORD);

        assertThat(result.getResponse().getStatus()).isEqualTo(201);
        String body = result.getResponse().getContentAsString();
        assertThat(body).doesNotContain("password").doesNotContain("Hash");
        assertThat(com.jayway.jsonpath.JsonPath.<String>read(body, "$.email")).isEqualTo(email); // normalized

        User stored = users.findByEmail(email).orElseThrow();
        assertThat(stored.getPasswordHash()).startsWith("$argon2id$").doesNotContain(PASSWORD);
    }

    @Test
    void duplicateRegistrationIsRejectedEvenWithDifferentCase() throws Exception {
        String email = uniqueEmail();
        assertThat(api.register(email, PASSWORD).getResponse().getStatus()).isEqualTo(201);

        MvcResult dup = api.register(email.toUpperCase(), PASSWORD);

        assertThat(dup.getResponse().getStatus()).isEqualTo(409);
        assertThat(dup.getResponse().getContentAsString()).contains("EMAIL_ALREADY_REGISTERED");
    }

    @Test
    void weakOrInvalidInputIsRejectedWithValidationError() throws Exception {
        for (String weak : new String[] {"short1A!", "alllowercaseletters", "Password1234"}) {
            MvcResult r = api.register(uniqueEmail(), weak);
            assertThat(r.getResponse().getStatus()).as(weak).isEqualTo(400);
            assertThat(r.getResponse().getContentAsString()).contains("VALIDATION_ERROR").contains("password");
        }
        MvcResult badEmail = api.register("not-an-email", PASSWORD);
        assertThat(badEmail.getResponse().getStatus()).isEqualTo(400);
    }

    @Test
    void malformedJsonYieldsGenericBadRequestWithoutInternals() throws Exception {
        mvc.perform(post("/api/auth/register").contentType(MediaType.APPLICATION_JSON).content("{nope"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.code").value("BAD_REQUEST"))
                .andExpect(jsonPath("$.path").value("/api/auth/register"))
                .andExpect(jsonPath("$.timestamp").exists());
    }

    // ---- login --------------------------------------------------------------------------------------

    @Test
    void loginSucceedsReturnsAccessTokenAndHardenedCookie() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);

        MvcResult result = api.login(email, PASSWORD);

        assertThat(result.getResponse().getStatus()).isEqualTo(200);
        String body = result.getResponse().getContentAsString();
        assertThat(body).contains("accessToken").doesNotContain("refreshToken").doesNotContain("passwordHash");
        String setCookie = result.getResponse().getHeader("Set-Cookie");
        assertThat(setCookie).contains("refresh_token=").contains("HttpOnly").contains("SameSite=Strict")
                .contains("Path=/api/auth");
        assertThat(users.findByEmail(email).orElseThrow().getLastLoginAt()).isNotNull();
    }

    @Test
    void invalidLoginIsRejectedIdenticallyForUnknownUserAndWrongPassword() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);

        MvcResult wrongPassword = api.login(email, "Wrong-Password-123");
        MvcResult unknownUser = api.login(uniqueEmail(), "Wrong-Password-123");

        assertThat(wrongPassword.getResponse().getStatus()).isEqualTo(401);
        assertThat(unknownUser.getResponse().getStatus()).isEqualTo(401);
        assertThat(strip(wrongPassword)).isEqualTo(strip(unknownUser));
        assertThat(wrongPassword.getResponse().getContentAsString()).contains("INVALID_CREDENTIALS");
    }

    @Test
    void accountIsTemporarilyLockedAfterRepeatedFailures() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        for (int i = 0; i < 5; i++) {
            assertThat(api.login(email, "Wrong-Password-123").getResponse().getStatus()).isEqualTo(401);
        }
        // Correct password is now refused too, with the same generic error.
        MvcResult locked = api.login(email, PASSWORD);
        assertThat(locked.getResponse().getStatus()).isEqualTo(401);
        assertThat(locked.getResponse().getContentAsString()).contains("INVALID_CREDENTIALS");
    }

    @Test
    void disabledAccountCannotLogIn() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        User u = users.findByEmail(email).orElseThrow();
        u.setEnabled(false);
        users.save(u);

        assertThat(api.login(email, PASSWORD).getResponse().getStatus()).isEqualTo(401);
    }

    // ---- refresh / rotation / logout --------------------------------------------------------------

    @Test
    void refreshWorksAndRotatesTheToken() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        MvcResult login = api.login(email, PASSWORD);
        String first = refreshCookie(login);

        MvcResult refreshed = api.refresh(first);

        assertThat(refreshed.getResponse().getStatus()).isEqualTo(200);
        String second = refreshCookie(refreshed);
        assertThat(second).isNotBlank().isNotEqualTo(first);
        assertThat(accessToken(refreshed)).isNotBlank();

        // New token works, and the database never contains the raw values.
        assertThat(api.refresh(second).getResponse().getStatus()).isEqualTo(200);
        for (RefreshToken t : refreshTokens.findAll()) {
            assertThat(t.getTokenHash()).hasSize(64).isNotEqualTo(first).isNotEqualTo(second);
        }
    }

    @Test
    void reusingARotatedTokenRevokesTheWholeSession() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        String first = refreshCookie(api.login(email, PASSWORD));
        String second = refreshCookie(api.refresh(first));

        // Attacker (or racing client) replays the already-used token...
        MvcResult replay = api.refresh(first);
        assertThat(replay.getResponse().getStatus()).isEqualTo(401);

        // ...and the legitimate newest token is dead as well: theft detected, family revoked.
        assertThat(api.refresh(second).getResponse().getStatus()).isEqualTo(401);
    }

    @Test
    void logoutRevokesTheSessionAndClearsTheCookie() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        String token = refreshCookie(api.login(email, PASSWORD));

        MvcResult logout = api.logout(token);

        assertThat(logout.getResponse().getStatus()).isEqualTo(204);
        assertThat(logout.getResponse().getHeader("Set-Cookie")).contains("refresh_token=").contains("Max-Age=0");
        assertThat(api.refresh(token).getResponse().getStatus()).isEqualTo(401); // revoked refresh token rejected
    }

    @Test
    void logoutAllRevokesEverySessionOfTheUser() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        MvcResult a = api.login(email, PASSWORD);
        String deviceB = refreshCookie(api.login(email, PASSWORD));

        mvc.perform(post("/api/auth/logout-all").header("Authorization", "Bearer " + accessToken(a)))
                .andExpect(status().isNoContent());

        assertThat(api.refresh(refreshCookie(a)).getResponse().getStatus()).isEqualTo(401);
        assertThat(api.refresh(deviceB).getResponse().getStatus()).isEqualTo(401);
    }

    @Test
    void refreshAndLogoutRequireTheCsrfHeaderAndAllowedOrigin() throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        String token = refreshCookie(api.login(email, PASSWORD));
        var cookie = new jakarta.servlet.http.Cookie("refresh_token", token);

        mvc.perform(post("/api/auth/refresh").cookie(cookie))
                .andExpect(status().isForbidden()).andExpect(jsonPath("$.code").value("CSRF_REJECTED"));
        mvc.perform(post("/api/auth/refresh").cookie(cookie).header("X-Requested-With", "x")
                        .header("Origin", "https://evil.example"))
                .andExpect(status().isForbidden());
        mvc.perform(post("/api/auth/refresh").cookie(cookie).header("X-Requested-With", "x")
                        .header("Origin", "http://localhost:5173"))
                .andExpect(status().isOk());
    }

    @Test
    void refreshWithoutCookieIsRejected() throws Exception {
        assertThat(api.refresh(null).getResponse().getStatus()).isEqualTo(401);
    }

    private static String strip(MvcResult r) throws Exception {
        // Everything except the timestamp must match for the two failure causes.
        return r.getResponse().getContentAsString().replaceAll("\"timestamp\":\"[^\"]*\"", "");
    }
}
