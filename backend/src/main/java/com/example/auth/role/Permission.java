package com.example.auth.role;

import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.Id;
import jakarta.persistence.Table;
import lombok.Getter;
import lombok.NoArgsConstructor;

@Entity
@Table(name = "permissions")
@Getter
@NoArgsConstructor
public class Permission {

    @Id
    private Short id;

    @Column(nullable = false, unique = true, length = 100)
    private String name;

    private String description;
}
