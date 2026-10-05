package com.example.auth.security;

import com.example.auth.support.ApiClient;
import com.example.auth.support.IntegrationTest;
import com.jayway.jsonpath.JsonPath;
import com.nimbusds.jose.JWSObject;
import com.nimbusds.jose.crypto.RSASSAVerifier;
import com.nimbusds.jose.jwk.JWKSet;
import com.nimbusds.jose.jwk.RSAKey;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.test.web.servlet.MockMvc;

import java.util.List;

import static com.example.auth.support.ApiClient.PASSWORD;
import static com.example.auth.support.ApiClient.accessToken;
import static com.example.auth.support.ApiClient.uniqueEmail;
import static org.assertj.core.api.Assertions.assertThat;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

/** The claims/keys contract the ASP.NET Core Todo API relies on. Change it deliberately. */
@IntegrationTest
class TokenContractIT {

    @Autowired MockMvc mvc;

    @Test
    void accessTokenCarriesTheDocumentedClaimsAndVerifiesWithPublishedKey() throws Exception {
        ApiClient api = new ApiClient(mvc);
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        String token = accessToken(api.login(email, PASSWORD));

        // Fetch the public key exactly as a resource API would.
        String jwksJson = mvc.perform(get("/.well-known/jwks.json")).andExpect(status().isOk())
                .andReturn().getResponse().getContentAsString();
        assertThat(jwksJson).doesNotContain("\"d\"").doesNotContain("\"p\"").doesNotContain("\"q\"");
        RSAKey publicKey = (RSAKey) JWKSet.parse(jwksJson).getKeys().get(0);

        JWSObject jws = JWSObject.parse(token);
        assertThat(jws.getHeader().getAlgorithm().getName()).isEqualTo("RS256");
        assertThat(jws.getHeader().getKeyID()).isEqualTo(publicKey.getKeyID());
        assertThat(jws.verify(new RSASSAVerifier(publicKey))).isTrue();

        String payload = jws.getPayload().toString();
        assertThat(JsonPath.<String>read(payload, "$.iss")).isEqualTo("auth-api");
        assertThat(JsonPath.<String>read(payload, "$.email")).isEqualTo(email);
        assertThat(JsonPath.<String>read(payload, "$.sub")).matches("[0-9a-f-]{36}");
        assertThat(JsonPath.<List<String>>read(payload, "$.aud")).contains("auth-api", "todo-api");
        assertThat(JsonPath.<List<String>>read(payload, "$.roles")).containsExactly("USER");
    }

    @Test
    void discoveryDocumentPointsAtTheKeySet() throws Exception {
        mvc.perform(get("/.well-known/openid-configuration"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.issuer").value("auth-api"))
                .andExpect(jsonPath("$.jwks_uri").value(org.hamcrest.Matchers.endsWith("/.well-known/jwks.json")));
    }
}
