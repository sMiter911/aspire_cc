package com.example.auth.security;

import com.example.auth.config.AppProperties;
import com.example.auth.exception.TooManyRequestsException;
import com.github.benmanes.caffeine.cache.Cache;
import com.github.benmanes.caffeine.cache.Caffeine;
import io.github.bucket4j.Bandwidth;
import io.github.bucket4j.Bucket;
import io.github.bucket4j.ConsumptionProbe;
import org.springframework.stereotype.Service;

import java.time.Duration;

/**
 * In-memory token-bucket limiter keyed by (policy, subject). Idle buckets are evicted so the map cannot grow
 * without bound. Single-instance only: with several API replicas move the buckets to a shared store.
 */
@Service
public class RateLimitService {

    private final AppProperties.RateLimit config;
    private final SecurityEvents events;
    private final Cache<String, Bucket> buckets = Caffeine.newBuilder()
            .maximumSize(100_000)
            .expireAfterAccess(Duration.ofHours(1))
            .build();

    public RateLimitService(AppProperties props, SecurityEvents events) {
        this.config = props.rateLimit();
        this.events = events;
    }

    public void check(String policyName, AppProperties.RateLimit.Policy policy, String subject, String ip) {
        if (!config.enabled()) {
            return;
        }
        Bucket bucket = buckets.get(policyName + ':' + subject, k -> Bucket.builder()
                .addLimit(Bandwidth.builder().capacity(policy.capacity())
                        .refillIntervally(policy.capacity(), policy.window()).build())
                .build());
        ConsumptionProbe probe = bucket.tryConsumeAndReturnRemaining(1);
        if (!probe.isConsumed()) {
            events.rateLimited(policyName, ip);
            long retry = Math.max(1, Duration.ofNanos(probe.getNanosToWaitForRefill()).toSeconds());
            throw new TooManyRequestsException(retry);
        }
    }

    public AppProperties.RateLimit.Policy loginPolicy() {
        return config.login();
    }

    public AppProperties.RateLimit.Policy registerPolicy() {
        return config.register();
    }

    public AppProperties.RateLimit.Policy refreshPolicy() {
        return config.refresh();
    }
}
