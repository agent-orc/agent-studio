FROM python:3.13-alpine

RUN apk add --no-cache git
WORKDIR /scenario
COPY testsupport/scenario/deployment-scenario.json ./deployment-scenario.json
COPY testsupport/scenario/deployment-scenario.schema.json ./deployment-scenario.schema.json
COPY testsupport/scenario/run_scenario.py ./run_scenario.py

ENTRYPOINT ["python3", "/scenario/run_scenario.py"]
