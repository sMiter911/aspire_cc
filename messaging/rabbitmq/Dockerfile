# Production broker image: the committed topology is baked in; the broker user is added at container start from
# RABBITMQ_DEFAULT_USER / RABBITMQ_DEFAULT_PASS (same scheme the AppHost uses locally), so no secret is in the image.
FROM rabbitmq:4-management
COPY definitions.json /etc/rabbitmq/topology.json
COPY 20-definitions.conf /etc/rabbitmq/conf.d/20-definitions.conf
COPY entrypoint.sh /usr/local/bin/broker-entrypoint.sh
RUN sed -i 's/\r$//' /usr/local/bin/broker-entrypoint.sh && chmod +x /usr/local/bin/broker-entrypoint.sh
ENTRYPOINT ["broker-entrypoint.sh"]
CMD ["rabbitmq-server"]
