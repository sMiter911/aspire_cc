package com.example.auth.security;

import com.example.auth.support.ApiClient;
import com.example.auth.support.IntegrationTest;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.test.context.TestPropertySource;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;

import static org.assertj.core.api.Assertions.assertThat;

@IntegrationTest
@TestPropertySource(properties = {
        "app.rate-limit.login.capacity=3",
        "app.rate-limit.login.window=10m"
})
class RateLimitIT {

    @Autowired MockMvc mvc;

    @Test
    void loginIsRateLimitedAfterCapacityIsExhausted() throws Exception {
        ApiClient api = new ApiClient(mvc);
        String email = ApiClient.uniqueEmail();
        for (int i = 0; i < 3; i++) {
            assertThat(api.login(email, "Wrong-Password-123").getResponse().getStatus()).isEqualTo(401);
        }
        MvcResult limited = api.login(email, "Wrong-Password-123");

        assertThat(limited.getResponse().getStatus()).isEqualTo(429);
        assertThat(limited.getResponse().getHeader("Retry-After")).isNotBlank();
        assertThat(limited.getResponse().getContentAsString()).contains("RATE_LIMITED");
    }
}
