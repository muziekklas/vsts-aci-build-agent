FROM debian:stretch

ARG VSTS_VERSION=2.141.1

WORKDIR /agent
RUN useradd vsts

RUN apt-get update
RUN curl -sL https://deb.nodesource.com/setup_10.x | bash -

RUN apt-get install -y git python python-setuptools python-pip nodejs \
  && rm -rf /var/lib/apt/lists/* \
  && pip install wheel

RUN npm install -g yarn


ADD https://vstsagentpackage.azureedge.net/agent/$VSTS_VERSION/vsts-agent-linux-x64-$VSTS_VERSION.tar.gz .
RUN tar xzf vsts-agent-linux-x64-$VSTS_VERSION.tar.gz \
  && ./bin/installdependencies.sh \
  && chown -R vsts:vsts /agent

USER vsts

ENTRYPOINT ["/bin/bash", "-c", "./config.sh --unattended --replace && ./run.sh"]
