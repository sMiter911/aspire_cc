package com.example.auth.auth.dto;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

import static org.assertj.core.api.Assertions.assertThat;

class StrongPasswordTest {

    private final StrongPassword.Validator validator = new StrongPassword.Validator();

    @ParameterizedTest
    @ValueSource(strings = {"Correct-Horse-Battery-9", "aB3$aB3$aB3$", "lowercase and spaces 123"})
    void acceptsLongPasswordsWithEnoughCharacterClasses(String password) {
        assertThat(validator.isValid(password, null)).isTrue();
    }

    @ParameterizedTest
    @ValueSource(strings = {"Sh0rt!", "alllowercaseletters", "ALLUPPERCASE12345", "Password1234", "PASSWORD1234"})
    void rejectsShortSingleClassOrCommonPasswords(String password) {
        assertThat(validator.isValid(password, null)).isFalse();
    }

    @Test
    void rejectsOverlongPasswords() {
        assertThat(validator.isValid("aA1!".repeat(40), null)).isFalse();
    }
}
