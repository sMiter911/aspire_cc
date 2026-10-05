package com.example.auth.user.dto;

import org.springframework.data.domain.Page;

import java.util.List;

/** Stable JSON shape for pages (avoids serialising Spring's PageImpl internals). */
public record PageResponse<T>(List<T> content, int page, int size, long totalElements, int totalPages) {

    public static <T> PageResponse<T> of(Page<T> page) {
        return new PageResponse<>(page.getContent(), page.getNumber(), page.getSize(), page.getTotalElements(),
                page.getTotalPages());
    }
}
