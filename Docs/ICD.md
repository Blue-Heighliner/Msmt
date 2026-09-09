# Interface Control Document

## Mercury Secure Message Transport (MSMT)

Version 1.2

April 10, 2025

Prepared by:

The MITRE Corporation
7515 Colshire Drive
McLean, Virginia 22102

The view, opinions, and/or findings contained in this report are those of The MITRE Corporation
and should not be construed as an official Government position, policy, or decision unless
designated by other documentation.

© Copyright 2025 The MITRE Corporation
NOTICE: This technical data deliverable was developed using contract funds under
Basic Contract No. W56KGU-18-D-0004

## CHANGE HISTORY

| Date | Version |
|------|---------|
| 02 FEB 2022 | V1.0 (Initial) |
| 10 APR 2025 | V1.2 |

### SUMMARY CHANGE LOG

#### CMI V3.5

| Section / Figure / Table | Change Description |
|---------------------------|---------------------|
| | |

## Acknowledgements

The authors acknowledge these contributors for the development of MSMT and of this ICD

**Primary Developers:**

Jeff Bush

Martin Woscek

Wei Ben

**Peer Reviewers:**

Dr. Casey Reardon

**Task Leader:**

Joanne Williams

## TABLE OF CONTENTS

- SECTION 1. PREFACE ........................................................................................................................... 1
- SECTION 2. SCOPE ................................................................................................................................ 2
  - 2.1 Interface Identification .................................................................................................................. 2
    - 2.1.1 MSMT Advantages ............................................................................................................. 2
  - 2.2 Document Organization ................................................................................................................ 3
  - 2.3 Definitions ..................................................................................................................................... 3
  - 2.4 Limitations and Restrictions.......................................................................................................... 3
- SECTION 3. APPLICABLE DOCUMENTS .......................................................................................... 4
  - 3.1 Normative Documents................................................................................................................... 4
  - 3.2 Informative Documents ................................................................................................................. 4
- SECTION 4. MSMT CHARACTERISTICS ........................................................................................... 5
  - 4.1 Multiple Modes of Operation ........................................................................................................ 5
    - 4.1.1 Message Mode .................................................................................................................... 5
    - 4.1.2 Message Mode with Rekeying ............................................................................................ 6
    - 4.1.3 Session Mode ...................................................................................................................... 6
  - 4.2 Mutual Peer Authentication........................................................................................................... 6
  - 4.3 Reachability Verification .............................................................................................................. 6
  - 4.4 Quality of Service (QoS) ............................................................................................................... 6
  - 4.5 Callbacks ....................................................................................................................................... 7
  - 4.6 MSMT Operational Risks ............................................................................................................. 7
    - 4.6.1 TLS Session Setup Failure .................................................................................................. 7
    - 4.6.2 Incompatible SSL Middleboxes.......................................................................................... 7
    - 4.6.3 Compatibility Across Versions of MSMT .......................................................................... 7
- SECTION 5. MSMT INTERFACE SPECIFICATION ........................................................................... 8
  - 5.1 General Concept of Operation....................................................................................................... 8
    - 5.1.1 Session Mode Concept of Operation ................................................................................ 10
  - 5.2 TLS Payload On-Wire Representation ........................................................................................ 11
  - 5.3 TLS Configuration Invariants ..................................................................................................... 12
    - 5.3.1 Public Key Certificates ..................................................................................................... 13
    - 5.3.2 Cipher Suites ..................................................................................................................... 13
    - 5.3.3 "supported_versions" Extension ....................................................................................... 13
    - 5.3.4 "server_name" Extension.................................................................................................. 13
- SECTION 6. MSMT API PROTOTYPES ............................................................................................. 14
  - 6.1 C++ Development Environment ................................................................................................. 14
    - 6.1.1 Build Environment ............................................................................................................ 14
    - 6.1.2 OpenSSL Dependencies ................................................................................................... 14
  - 6.2 Java Development Environment ................................................................................................. 15
    - 6.2.1 Build Environment ............................................................................................................ 15

### LIST OF FIGURES

- Figure 5-1. Architecture Diagram for Message Mode ................................................................................ 10
- Figure 5-2. MSMT Message Header Fields ................................................................................................ 11
- Figure 5-3. MSMT Flag Field ..................................................................................................................... 12

## SECTION 1. PREFACE

Many military messaging systems predate the internet and were designed to operate over a complex
topology of serial, point-to-point, terrestrial links. The physical equipment and legacy technologies used
to carry these messages are no longer cost-effective to operate and maintain. Of greater concern is the
inadequacy of serial messaging systems to provide resilient message delivery and authenticity guarantees.

Transitioning terrestrial messaging systems to operate over modern, secure, Internet Protocol (IP)
networks is an imperative. A number of interim, IP-based solutions have been developed and deployed
across segments of the messaging community over the past two decades, but a critical factor hindering
wider adoption is the lack of an open source, non-proprietary standard that incorporates modern security
features.

This Interface Control Document (ICD) specifies an open, secure, standards-based API for secure
messaging. Though the Application Programming Interface (API) is presented in the context of Allied
Communications Publication 128 (ACP 128) format messaging, it is equally applicable to other
messaging systems and formats and could be used to enable interoperability across disparate systems.

This technical data deliverable was developed using contract funds under Basic Contract No. W56KGU-
18-D-0004.

## SECTION 2. SCOPE

### 2.1 Interface Identification

This ICD defines the Mercury Secure Message Transport (MSMT), a secure communications interface for
legacy message handling applications. It specifically addresses the case for secure transmission and
reception of messages over Internet Protocol (IP) terrestrial networks. Interface features required to
support wireless communication alternatives are not considered in this version of the ICD.

The ongoing move from legacy serial links to IP network communications is motivated, in part, by
DISA's plan to eliminate or change the terms of service on all TDM services. While native IP message
protocols do exist, there is no ubiquitous standard available to system and application developers to
realize a common IP based interface for the larger messaging community. Earlier protocols such as
Virtual Circuit Protocol (VCP) possess significant shortcomings related to the lack of guaranteed end-to-
end message-level integrity and protocol maintainability. These realities have prompted work on a new
messaging transport standard: MSMT.

Additional motivation for work on a new messaging transport alternative is the heightened awareness of
insider threat and the potential for grave impact to national security if that threat is left unchecked. VCP,
for example, does not encrypt messages across the network nor does it ensure message authenticity.
Currently deployed Type-1 encryptors do provide both of these security services. However, message
traffic traversing edge networks (red side) are largely unprotected from eavesdropping, spoofing, and
tampering.

MSMT encompasses both an interface for developers of message handling applications and a fixed
configuration of Transport Layer Security (TLS) v1.3. It provides for end-to-end message protection over
and above existing Type-1 encrypted WAN links. The core application programming interface to MSMT
Version 1 is documented in Section 6.

#### 2.1.1 MSMT Advantages

Decoupling message processing from IP secure transport has a number of advantages. In particular,
message handling applications built against the MSMT ICD will:

- Maintain full control of all message processing logic and the handling of application-level
  message acknowledgements (where applicable)
- Be network interoperable
- Provide a consistent level of strong message-level integrity and confidentiality assurances
- Allow for uniform patching and upgrading of cryptographic algorithms (e.g., to post-quantum)
  with minimal to no impact on the operation of message handling applications
- Provide transparent support for additional IP network communication models, namely one-to-
  many (multicast), broadcast, and message brokers
- Open the potential to stand-up a single organization to maintain future versions of this ICD and
  NSA-approved implementations

### 2.2 Document Organization

This ICD is organized as follows:

a. Section 2 defines the interface, describes the document's organization, and provides
   definitions of terms and abbreviations along with any associated limitations and restrictions.
b. Section 3 presents a list of applicable reference documents that provide requirements and
   design details not contained in this ICD.
c. Section 4 identifies the MSMT software interface.
d. Section 5 provides the interface specification and concept of operation.
e. Section 6 details the MSMT prototype API implemented in C++.

### 2.3 Definitions

The following is a list of terms, abbreviations, and acronyms that appear in this ICD.

| Term | Definition |
|------|------------|
| ACK | Positive Acknowledgment |
| ACP | Allied Telecommunications Publication |
| API | Application Program Interface |
| ASCII | American Standard Code for Information Interchange |
| ICD | Interface Control Document |
| IP | Internet Protocol |
| MPLS | Multiprotocol Label Switching |
| NACK | Negative Acknowledgment |
| TCP | Transmission Control Protocol |
| TLS | Transport Layer Security |
| VCP | Virtual Circuit Protocol |

### 2.4 Limitations and Restrictions

There are no limitations or restrictions related to the interface identified in this ICD.

## SECTION 3. APPLICABLE DOCUMENTS

The following documents were used in the development of this ICD and can be used as sources of
additional information.

### 3.1 Normative Documents

NIST Special Publication 800-52 Revision 2 – Guidelines for the Selection, Configuration, and Use of
Transport Layer Security (TLS) Implementations, August 2019.

### 3.2 Informative Documents

FIPS PUB 180-4 – Secure Hash Standard (SHS), August 2015.

NIST Special Publication 800-131A Revision 2 – Transitioning the Use of Cryptographic Algorithms and
Key Lengths, March 2019.

RFC 5280 – Internet X.509 Public Key Infrastructure Certificate and Certificate Revocation List (CRL)
Profile, May 2008.

RFC 8446 – The Transport Layer Security (TLS) Protocol Version 1.3, August 2018.

## SECTION 4. MSMT CHARACTERISTICS

The Application Program Interface (API) specified in this ICD defines a set of library calls for message
handling applications. Responsibility for secure message delivery is delegated to a library of functions
implemented in accordance with Section 5. There is no requirement for library implementers to preserve
function call names and signatures included in this ICD. Implementers SHOULD preserve the spirit of
each function call, however.

The Transmission Control Protocol (TCP) enables connection-oriented, reliable, in-order delivery of
application-level data streams over IP networks. Transport Layer Security (TLS) is a separate protocol
that operates over established TCP connections or sessions. It enables peer authentication, data integrity,
and confidentiality services. TLS v1.3 includes numerous security enhancements, many prompted by
highly publicized vulnerabilities found in previous versions of the specification and in third-party
implementations. For example, TLS v1.3 mitigates an entire class of record layer vulnerabilities by
limiting the selection of symmetric encryption algorithms to those supporting authenticated encryption
with associated data (AEAD) modes. TLS v1.3 also provides key agreement algorithms capable of perfect
forward secrecy (PFS). With PFS, private keys that are compromised cannot be used to recover unique
session keys and thereby decrypt past recorded sessions. In-depth coverage of TLS protocol operation and
internals is beyond the scope of this document.

Developers are faced with many decisions when designing network applications to run over TLS. Some
decisions will be based on the lack of availability of services in a target network environment, such as a
public key infrastructure (PKI). Other decisions will require knowledge of cryptographic primitives and
the tradeoffs between performance and security strength. Still others may deal with potential TLS version
interoperability issues.

MSMT is not a network protocol[^1] but a specific, system-wide configuration of TLS. TLS was selected as
the basis for resilient messaging in part due to reasons highlighted above and in part due to its relative
maturity and considerable vetting by the network security community over many years. Numerous TLS
implementations, such as OpenSSL (https://www.openssl.org), are freely available.

Standardization of the interface to MSMT gives message handling developers a simplified interface to a
highly secure and resilient communications service. The following sub-sections highlight key design
decisions made in collaboration with messaging stakeholders.

[^1]: In the latest version of MSMT ten bytes of overhead are prepended to each message to facilitate connection establishment, connection administration, and message reception.

### 4.1 Multiple Modes of Operation

#### 4.1.1 Message Mode

Long-lived network connections carrying multiple messages between IP network nodes give adversaries
more opportunities to perform traffic analyses, exploit flaws in TLS implementations, or even recover
TLS session keys. In order to mitigate these risks, by default MSMT establishes separate TLS sessions for
each message transmission. This is referred to as "Message Mode".

This decision leads to an increase in network overhead and message latency. However, message timing
requirements, from origination to ultimate destination, are typically on the order of tens of seconds. TLS
session instantiation is sub-second even on modest hardware platforms unless communicating over high-
latency communications links. Please note the term "message" as used here is from the point of view of
MSMT, not the application level. When appropriate, the messaging application is recommended to bundle
multiple short application-level messages (e.g. ACP-128) together within one MSMT message to
minimize network overhead and maximize throughput. See Section 5.2 for details of MSMT messages.

#### 4.1.2 Message Mode with Rekeying

Instead of forcing MSMT to completely tear down and re-establish the TLS connection between every
message, the library supports TLS rekeying between messages. In this mode, MSMT will initiate a TLS
rekey operation that generates a new set of traffic encryption keys within the TLS connection. MSMT
allows the client and server to configure the number of rekeys allowed before a full reset of the TLS
connection is performed. Enabling rekey appropriately reduces the overhead of MSMT message mode
with a very minimal decrease in security posture as traffic encryption keys are still being regenerated
between each message.

#### 4.1.3 Session Mode

Session mode is designed for environments where the overhead of message mode is a concern, for
example due to limited bandwidth and reliability of the network links. In session mode, the MSMT client
and server agree to allow one TLS connection to support an arbitrary number MSMT message exchanges,
until the connection timer expires. The maximum lifetime of the TLS connection is determined during a
negotiation process that must take place immediately after the TLS connection is created. The negotiation
process is described in Section 5.1.1.

TLS connections are required to disconnect after being idle for a period of time. Keep-alive messages
designed to prevent this have been added to this ICD. The MSMT endpoints are configured with a keep-
alive timer that will automatically generate the MSMT keep-alive message if a session-mode TLS
connection is active and the idle timer has expired since either the last MSMT message or keep-alive was
sent.

### 4.2 Mutual Peer Authentication

Given the criticality of legacy messaging to the nation's security, enforcement of mutual node
authentication is paramount. Message consumers must have reason to trust that message originators are
authentic. There are two main approaches to authenticating TLS peers: utilizing public-key certificates
(asymmetric cryptography) and pre-shared keys (symmetric cryptography). MSMT Version 1 was
primarily designed to utilize public-key cryptography but can also utilize pre-shared keys (PSK) in
situations where enterprise PKI is unavailable. The decision to use self-signed certificates, PSK, or
leverage an existing PKI is beyond the scope of this ICD.

### 4.3 Reachability Verification

A "ping-like" feature of msmt_send() is available for client-to-server reachability checks. Message
originating applications can go through all the steps of bringing up a TLS connection to a remote server
and send an encrypted test message. If successful, server applications echo back the test message and tear
down the TLS connection. Test messages are never delivered to server applications.

### 4.4 Quality of Service (QoS)

DiffServ code points can be applied to TLS session packets via the msmt_send() call. Acceptable values
are highly network dependent. MSMT implementations will mark packets as instructed by the messaging
application. Enforcement of standard values and/or remarking of packets is not supported in MSMT
Version 1.

### 4.5 Callbacks

MSMT supports callbacks. Messaging application developers register callback function(s) with the
MSMT library upon initialization. Callback functions contain application-level logic. MSMT triggers
application callback functions, if provided, at known points in the message transmission and reception
process. Examples include the notification of received messages at a server instance. Numerous logging
callback functions are available for auditing and troubleshooting purposes.

### 4.6 MSMT Operational Risks

Unless introduced carefully, new security measures applied to legacy message handling applications
could negatively impact the messaging mission. Such risks, and associated mitigations, are highlighted
below. Other risks may be added to future versions of this ICD.

#### 4.6.1 TLS Session Setup Failure

Given the complexities of TLS session setup (most hidden from applications via the MSMT API), there
will be occasions when message originators and/or relay nodes will be inhibited from transmitting or
forwarding messages to next-hop destinations. Obviously, this event could have very serious
consequences to the need for assured delivery. Potential causes of message transmission failures range
from expired public key certificates, explicit connection blocking by network infrastructure or security
services, IP routing loops, and WAN link failures to crashed destination nodes.

MSMT cannot influence many of the scenarios presented above. However, the API provides mechanisms
that applications can use to increase the level of operator trust in application reachability and current
network conditions.

MSMT implementations will refuse to install:

- Certificate chains that include expired certificates
- Certificates with invalid key lengths (see NIST 800-131A R2)
- Certificates with invalid signature algorithms or key lengths (see NIST 800-131A and FIPS 180-4)
- Certificates failing the check for the consistency of a private key with the corresponding
  certificate that is loaded

#### 4.6.2 Incompatible SSL Middleboxes

MSMT enforces TLS v1.3-based connections only. Negotiating sessions down to TLS v1.2 or earlier
versions is not possible. Some SSL inspection network elements may not be compatible.

#### 4.6.3 Compatibility Across Versions of MSMT

Features introduced in MSMT v1.2, including the new Message ID field, changed the size and format of the MSMT
header. As a result, versions 1.2 and later are not backward compatible with MSMT 1.1 or 1.0. Since MSMT had not
been widely deployed before the release of version 1.2, this risk should not be significant. A more significant issue
moving forward will be to ensure MSMT clients and servers are configured in compatible fashions, such as using
compatible pre-shared keys and operational modes.

## SECTION 5. MSMT INTERFACE SPECIFICATION

### 5.1 General Concept of Operation

This section describes the MSMT interface for resilient message transmission and reception. While the
description in this sub-section primarily describes MSMT in the default message mode, the sequence of
API calls by the MSMT client and server are the same between the three modes outlined in Section 4.1. In
general, once the connections are configured and established, the operation of the different modes of
MSMT are transparent to the message applications using MSMT.

Figure 5-1 is a graphical depiction of the steps involved in secure message transfer between a messaging
client and server (i.e., message sender and receiver processes) when using default message mode. Vertical
lines inside the messaging client and server represent the separation between application logic and the
responsibilities of the secure transport. Arrows from the Message Application toward the Secure
Transport indicate when MSMT API calls are invoked. Arrows from the Secure Transport toward the
Message Application indicate MSMT API return values or callback functions being invoked. Finally,
arrows between the client and server represent TLS protocol interactions across the WAN.

1. **Initialize Secure Transport (Client and Server)**

   Before messages can be sent or received, MSMT libraries must be supplied with
   information required by TLS. Examples include the locations of public key certificate files,
   private keys, and pre-shared keys. If the node will be configured as a client, the destination
   IP address or fully qualified hostname must be known. If the node will be configured as a
   server, the IP address of the listening interface must be specified.

   Multiple instantiations of MSMT clients or servers are required to send messages to more
   than one destination node or to receive messages from clients on interfaces in different IP
   domains respectively (the case where servers are registered in a DNS with multiple
   identities).

2. **Server Start**

   After MSMT is initialized, messaging applications will begin listening for incoming TLS
   connections on a pre-determined well-known port. This call operates asynchronously, that
   is, it is non-blocking.

3. **Message**

   At some point after MSMT initialization, the client will send a message to a remote MSMT
   peer. Note that MSMT performs no message processing. It sees messages as simply a string
   of bytes. Applications indicate the message length in the call to msmt_send().

   Depending on MSMT's configured operational mode and state, the call to msmt_send()
   may lead to one of a few possible actions. If running in session mode, the message will be
   sent over the existing TLS connection if available. If message mode with rekeying is used
   and a TLS connection has already been established, a rekey operation will be initiated if a
   connection exists and the rekey limit has not been reached. In all other cases the
   msmt_send() function call will initiate a new TLS connection with the remote peer.

   msmt_send() is a blocking call. It will return when the message is fully delivered and
   acknowledged by the remote peer node, or on error. Applications should always check the
   return code of msmt_send().

4. **Connection Thread**

   Individual client TLS connections are handled in separate threads transparent to the
   messaging application.

5. **Message Callback**

   An application-supplied callback function is invoked when a full message is received.
   (Setup of the message callback function is not shown in Figure 5-1.).

6. **Message Acknowledgement**

   Once the application determines that the message is valid, it notifies the sending node
   (client) of this fact using msmt_msgAck(). If message parsing fails, a negative
   acknowledgement is conveyed using msmt_msgNack().

   Directly following a call to msmt_msgAck() or msmt_msgNack(), the client begins the
   process of tearing down the TLS connection if using default message mode.

   The application will determine if message ACKs are to be used referencing Figure 5-2
   which shows the option flag that the application user would set that signals to the server
   that the ACK is requested for the sent message:

   > ACK requested/acknowledged (1) or not (0)

   The application uses this flag to indicate the request of an ACK per message, so by setting
   it to 1, that is telling the server that the client requests the ACK. The server uses the same
   flag, but it indicates ACK or no ACK per the message

   With callback functions support, the message payload ACKs should be performed by the
   respective messaging applications.

7. **Return Status**

   Once the TLS connection has terminated, the client-side call to msmt_send() returns an
   ACK message upon success; in the case of an error or problem with the handling of the
   message by the server, a NACK message is returned by the server to the client and the
   appropriate error will be passed back to the user on the server side through the logging
   callback, if defined by the user on the server side. This same error message could be used
   as the NACK message to signal the user on the client side from the msmt_send().

   If the message cannot be sent at all by the client, a NULL pointer is returned, and it is up to
   the client caller (i.e. messaging application) to decide how to proceed.

**Figure 5-1. Architecture Diagram for Message Mode**

*(Original document contains a graphical sequence diagram depicting the seven numbered steps above as arrows between a Message Application, Secure Transport, and remote peer across a WAN.)*

#### 5.1.1 Session Mode Concept of Operation

The concept of operation for MSMT Session Mode is generally the same as described in Section 5.1 but
includes a new negotiation process used immediately after the TLS connection establishment that is
described in this section.

To initiate a session mode connection, the client sends an MSMT message with the Session Mode Timer
Negotiation and Message Success flags set (see Section 5.2) immediately after the TLS connection is
established. The server will only accept a message with the Session Mode Timer Negotiation flag set
when it is the very first message received over the TLS connection, otherwise Message Mode is assumed,
and any Session Mode negotiation is rejected.

The client includes a proposed maximum lifetime for the TLS connection in its initial Session Mode
Negotiation message. The server responds to the session mode request message with a response that either
accepts or rejects the session mode connection. If the server accepts the session mode connection, it will
reply with its proposed maximum TLS connection time, which will be the lesser of either the proposed
maximum time proposed by the client or the server's own configured allowable maximum connection
time.

When an MSMT client sends a session mode negotiation message, it MUST drop any response it receives
from the server that does not have the Session Mode Timer Negotiation flag set, at which point the TLS
connection MUST be closed due to a failed negotiation process.

Once the session mode connection is established, the TLS connection can be used to exchange MSMT
messages until the connection times out, at which time the MSMT client must negotiate a new TLS
connection. MSMT will generate reachability check messages to act as keepalives that prevent the
Session Mode connection from being closed due to being idle. To meet DoD requirements, the timing
between keepalives is randomized between three and five minutes by default. Those time bounds are
defined by constants in the code and can be modified to meet operational needs.

### 5.2 TLS Payload On-Wire Representation

MSMT performs no message processing. Application-level messages and acknowledgements are treated
as variable length byte sequences of binary values.

> **NOTE:** The MSMT message/payload can bundle multiple application-level messages to increase
> the network throughput of the application, which can be regulated within software applications by
> creating a settable maximum bundle size parameter (e.g. 500kB) limiting the quantity of legacy
> messages (e.g. ACP-128) that can be bundled within a single MSMT message. To avoid delaying
> legacy message delivery when the queue is almost empty, there should be no minimum bundle
> size, such that if only one legacy message is available in the queue, it gets bundled and delivered
> immediately without having to wait for other messages to complete a maximum-size bundle.
> Implementation specific details of message/payload bundling are at the discretion of the
> application developer and out of scope for this ICD.

To facilitate message reception, multiple operation modes, error handling, and application cross-layer
signaling, MSMT v1.2 prepends additional information in a ten-byte header. The on-wire message format
for client-to-server and server-to-client exchanges is shown in Figure 5-2.

```
 0                        1                       2                        3

 0 1 2 3 4 5 6 7 8 90 1 2 3 4 5 6 7 8 90 1 2 3 4 5 6 7 8 90 1
    API ver:8         rsv:8                  flags:16
          Message ID: 16                Message length:32
        Message length:32            Message data (variable)
                     Message data (variable)
```

**Figure 5-2. MSMT Message Header Fields**

The high-order, eight-bit API ver field MUST be set based on the MSMT feature set supported. Valid
values of the version field are as follows:

- API Ver = 1: MSMT version 1.1 and earlier that does not expose the RSV field to applications
  (i.e. the RSV field must be 0)
- API Ver = 2: MSMT version 1.1 and earlier that exposes the RSV field to applications for their
  custom usage
- API Ver = 3: MSMT version 1.2 with added support for Session Mode and Message IDs. The
  RSV field is not exposed and must be set to 0.

If MSMT servers receive a message with any other API version value, the TLS connection MUST
terminate after sending an acknowledgement message, without processing the message. The
acknowledgement message MUST set the invalid preamble flag and have a zero-length message
acknowledgement payload. The blocking client application call to msmt_send() SHOULD return with an
appropriate error code.

The flag field provides an in-band communication channel between clients and servers. Future versions of
this ICD may include other API features that will require additional flags to be defined. Flags are set
indirectly by client and server applications through the MSMT API. In Version 1, four message flags are
defined (see Figure 5-3).

Servers use the message success flag to indicate whether a client message is valid, as determined by the
server-side messaging application. Clients use the ACK-requested flag to request a message
acknowledgement from the connected server. In turn, servers set the ACK-acknowledged flag on the
application-layer message response. Acknowledgement messages with a payload length of zero are valid.
The reachability check flag provides a mechanism for clients to verify TLS connectivity to servers
without sending authentic messages. Motivation for this feature is summarized in Section 4.3. Upon
receiving a reachability check message, MSMT servers respond to clients using a message
acknowledgement with the reachability check flag set and the original message copied into the message
acknowledgement payload. Reachability check message payloads MUST not be delivered to MSMT
server applications. The invalid message header flag is set by servers to indicate reception of malformed
client message headers.

The Session Mode Negotiation flag is set to signify the message is either requesting a new session mode
connection or responding to a session mode negotiation request. When this flag is set, the message may
contain the proposed session maximum lifetime of the session. The client will use this flag in combination
with the message success/failure flag set in its initial Session Mode request message. When the server
responds to a Session Mode request, it uses this flag in combination with message success/failure flag and
invalid preamble flag to signify the result of the session mode negotiation to the client.

The three remaining flag bits MUST be set to zero. Otherwise, servers MUST terminate the TLS
connection after sending an acknowledgement message, without processing the message. The
acknowledgement message MUST set the invalid preamble flag and have a zero-length message
acknowledgement payload. The blocking client application call to msmt_send() SHOULD return with an
appropriate error code.

The Message ID field defines a unique 16-bit random ID to each message. The corresponding Message
ID is included in the acknowledgment message to validate the message being acknowledged and support
future logging/forensics operations.

Finally, the message length field is encoded in network byte order (big endian). It contains the length of
the following message, or message acknowledgement, in bytes.

```
   Flag
0 0 0 X X X X X
| | | | | | | L         [server]                 Message Success (1) or Failure (0)
| | | | | | L--         [client/server]          ACK requested/acknowledged (1) or not (0)
| | | | | L----         [client/server]          Reachability Check
| | | | L------         [client/server]          Invalid Preamble / Mode Unsupported
| | | L--------         [client/server]          Session Mode Negotiation
---------------         [client/server]          Reserved for Future Use
```

**Figure 5-3. MSMT Flag Field**

### 5.3 TLS Configuration Invariants

The decision to limit TLS v1.3 configurations to a well-understood subset tends to enhance messaging
application reliability, expedite troubleshooting, and help organizations respond more rapidly to a range
of future security threats. To that end, MSMT implementations must adhere to TLS v1.3 guidance
provided by NIST SP 800-52 Revision 2, Sections 3 and 4. The following sub-sections highlight several
core requirements and notable exceptions.

#### 5.3.1 Public Key Certificates

MSMT TLS connections must not be allowed to succeed unless MSMT server and client certificates
adhere to NIST server and client profile definitions and are deemed valid after revocation checks.

MSMT implementations may utilize domain-issued certificates (e.g. PSK) when third-party certificate
authorities are not available.

#### 5.3.2 Cipher Suites

MSMT clients must include this list of cipher options in the ClientHello. The ordering of cipher suites
must be preserved.

- TLS_CHACHA20_POLY1305_SHA256
- TLS_AES_256_GCM_SHA384

#### 5.3.3 "supported_versions" Extension

MSMT is designed to operate over TLS v1.3 only. Therefore, TLS client "supported_versions" extension
will include a single element list. MSMT servers must abort connections from clients attempting to
negotiate down to any lesser TLS version.

- TLS v1.3 (0x0304)

#### 5.3.4 "server_name" Extension

A Server Name Indication (SNI) extension is necessary when TLS servers are known by more than one
DNS hostname. Virtualized server environments are often used to motivate this need. Without an SNI, it
is impossible for TLS to determine which server should handle an incoming connection, or which set of
security credentials to incorporate.

MSMT clients must include the "server_name" extension in every ClientHello. Fully qualified DNS
hostnames are required. MSMT servers must abort connections from clients attempting to connect
without the "server_name" extension or from clients that supply invalid hostnames.

## SECTION 6. MSMT API PROTOTYPES

C++ and Java MSMT prototypes were developed concurrently with this ICD. These efforts were
motivated by a need to:

- Confirm that open source and freely available crypto libraries can meet ICD requirements
- Demonstrate the benefits of encapsulating all message security operations behind a standard API

Basic build instructions and software dependencies for each prototype follow. The software packages and
versions listed in the build environments below should be treated as minimum requirements, unless
otherwise stated. The MSMT packages have been tested across multiple Linux distributions including
CentOS, Ubuntu, and Rocky. At this time the MSMT implementations have no known limitations with
respect to particular distributions of Linux.

Complete MSMT API documentation is generated during the build process. A Docker image that includes
MSMT and its build dependencies is also available upon request.

### 6.1 C++ Development Environment

#### 6.1.1 Build Environment

```
C++ Compiler: gcc/g++ version 4.8.5 20150623 (Red Hat 4.8.5-36) (GCC)
cmake: version 2.8.12.2
make: GNU Make 3.82 (Built for x86_64-redhat-linux-gnu)

Optional: eclipse: version Oxygen.3a Release (4.7.3a)
Optional: doxygen: version 1.8.5
```

#### 6.1.2 OpenSSL Dependencies

```
OpenSSL source code is available at https://www.openssl.org/source/

The C++ MSMT prototype requires OpenSSL version 1.1.1. The current version of MSMT was tested
with OpenSSL version 1.1.1w, the latest release of OpenSSL 1.1.1 at the time of writing.
Note: Versions older than 1.1.1d do not fully support TLSv1.3 features.
For this build environment, the following commands were used to configure OpenSSL and
development support.

$ ./config --prefix=/usr/local/ssl --openssldir=/usr/local/ssl '-Wl,-rpath,$(LIBRPATH)'
$ make
$ make test
$ sudo make install
```

### 6.2 Java Development Environment

#### 6.2.1 Build Environment

```
Build environment:
Jave 21 is required to build MSMT v1.2.
The code was tested and built with OpenJDK build 21.0.2+13

$ant-1.9.4     #ant is the preferred build tool. If not available, use javac/java to build the jar file

$ant doc       #generates the MSMT API documentation
```
