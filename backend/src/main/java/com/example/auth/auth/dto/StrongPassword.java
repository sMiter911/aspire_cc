package com.example.auth.auth.dto;

import jakarta.validation.Constraint;
import jakarta.validation.ConstraintValidator;
import jakarta.validation.ConstraintValidatorContext;
import jakarta.validation.Payload;

import java.lang.annotation.Documented;
import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;
import java.util.Locale;
import java.util.Set;

/**
 * Password policy: 12-128 characters, at least three of {lower, upper, digit, symbol}, not a well-known
 * password. Length is the main control (NIST 800-63B); the class rule is a cheap extra.
 * Extend the deny-list or plug in a breached-password service for production.
 */
@Documented
@Constraint(validatedBy = StrongPassword.Validator.class)
@Target({ElementType.FIELD, ElementType.PARAMETER})
@Retention(RetentionPolicy.RUNTIME)
public @interface StrongPassword {

    String message() default "Password must be 12-128 characters and include at least three of: lowercase, "
            + "uppercase, digit, symbol";

    Class<?>[] groups() default {};

    Class<? extends Payload>[] payload() default {};

    int MIN = 12;
    int MAX = 128;

    class Validator implements ConstraintValidator<StrongPassword, String> {

        private static final Set<String> COMMON = Set.of(
                "password1234", "password12345", "passw0rd1234", "qwerty123456", "123456789012",
                "letmein12345", "welcome12345", "administrator1", "iloveyou1234", "changeme1234");

        @Override
        public boolean isValid(String value, ConstraintValidatorContext context) {
            if (value == null) {
                return true; // @NotBlank reports null/blank
            }
            if (value.length() < MIN || value.length() > MAX) {
                return false;
            }
            if (COMMON.contains(value.toLowerCase(Locale.ROOT))) {
                return false;
            }
            boolean lower = false, upper = false, digit = false, symbol = false;
            for (int i = 0; i < value.length(); i++) {
                char c = value.charAt(i);
                if (Character.isLowerCase(c)) lower = true;
                else if (Character.isUpperCase(c)) upper = true;
                else if (Character.isDigit(c)) digit = true;
                else symbol = true;
            }
            int classes = (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);
            return classes >= 3;
        }
    }
}
