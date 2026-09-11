# Nokia SR Linux Complete Guide

Platform Overview
Nokia SR Linux

Device Types: 7220 IXR, SR Linux devices
CLI Style: Modern YANG-based configuration
Configuration: Requires candidate mode, explicit commit
Interface Command: show interface all
Routing Command: show network-instance default route-table

================================================================
CONFIGURATION MODE (applies to ALL configuration commands below)
================================================================
ALL configuration changes require:
1. enter candidate
2. set / ... (one or more commands)
3. commit now
After every configuration, VERIFY with the appropriate show/info command.

================================================================
COMMAND REFERENCE - OPERATIONAL (show/info)
================================================================

System Information Commands

show version - Displays chassis type, software version, hardware details, serial number

Interface Commands

show interface all - Display all interfaces with status
show interface brief - Display interface summary
show interface ethernet-1/1 - Display specific interface
show interface ethernet-1/1 detail - Display detailed interface stats
show interface ethernet-1/1 subinterface 0 - Display subinterface details

Routing Commands

show network-instance default route-table - Display routing table
show network-instance default route-table ipv4-unicast summary - IPv4 routing summary
show network-instance default route-table ipv6-unicast summary - IPv6 routing summary

BGP Commands (OPERATIONAL)

show network-instance default protocols bgp summary - BGP summary (peers, states, prefixes)
show network-instance default protocols bgp neighbor - All BGP neighbor details
show network-instance default protocols bgp neighbor X.X.X.X - Specific BGP neighbor detail
show network-instance default protocols bgp neighbor X.X.X.X received-routes ipv4 - Received routes from peer
show network-instance default protocols bgp neighbor X.X.X.X advertised-routes ipv4 - Advertised routes to peer
show network-instance default protocols bgp routes ipv4 summary - BGP IPv4 route summary
show network-instance default protocols bgp routes ipv4 - All BGP IPv4 routes
show network-instance default protocols bgp routes ipv6 summary - BGP IPv6 route summary
show network-instance default protocols bgp group - BGP peer group summary
info network-instance default protocols bgp - Show full BGP configuration

OSPF Commands (OPERATIONAL)

show network-instance default protocols ospf neighbor - OSPF neighbors
show network-instance default protocols ospf interface - OSPF interfaces
show network-instance default protocols ospf database - OSPF LSDB
show network-instance default protocols ospf routes - OSPF routes
show network-instance default protocols ospf status - OSPF process status
info network-instance default protocols ospf - Show full OSPF configuration

Static Route Commands (OPERATIONAL)

show network-instance default static-routes - Display static routes
info network-instance default static-routes - Show static route configuration

LLDP Commands

show system lldp neighbor - Display LLDP neighbors

Network Instance / VRF Commands (OPERATIONAL)

show network-instance summary - List all network instances (VRFs)
show network-instance * summary - Summary of all network instances
show network-instance VRFNAME route-table - Routes in specific VRF
show network-instance VRFNAME protocols bgp summary - BGP in specific VRF
info network-instance VRFNAME - Show full VRF configuration

VLAN / Bridge Domain Commands (OPERATIONAL)

info interface ethernet-1/X subinterface Y - Show VLAN tagging config
show network-instance MACVRFNAME bridge-table mac-table all - MAC table for bridge domain
info network-instance MACVRFNAME - Show MAC-VRF / bridge domain configuration

Routing Policy Commands (OPERATIONAL)

info routing-policy - Show all routing policies
info routing-policy policy POLICYNAME - Show specific policy
info routing-policy prefix-set PREFIXSETNAME - Show prefix set

ACL / Filter Commands (OPERATIONAL)

info acl - Show all ACL configuration
info acl ipv4-filter FILTERNAME - Show specific IPv4 filter
info acl ipv6-filter FILTERNAME - Show specific IPv6 filter
show acl ipv4-filter FILTERNAME entry * - Show filter entry match counts

System Configuration Commands (OPERATIONAL)

info system ntp - Show NTP configuration
info system dns - Show DNS configuration
info system logging - Show logging configuration
info system aaa - Show AAA configuration
show system ntp - Show NTP status
show system ntp server - Show NTP servers and synchronization

BFD Commands (OPERATIONAL)

show bfd session - Show all BFD sessions
show bfd session peer X.X.X.X - Show specific BFD session
info bfd - Show BFD configuration

CPU/Memory Monitoring

To check CPU usage execute the command: info from state / platform control A cpu all. In the response, look for the value under total > instant which represents the current CPU percentage. Evaluation logic: if CPU is below 50% respond with "CPU status normal: X% - System operating correctly", if CPU is between 50% and 70% respond with "CPU warning: X% - Monitor closely", if CPU is above 70% respond with "ALERT: CPU saturated at X% - System under high load, requires attention. Recommended actions: check processes with high consumption, verify for loops or traffic storms, consider load redistribution". When CPU exceeds 70% also execute: info from state / platform control A process to identify which process is consuming the most resources and include the top consuming processes in your response.
info from state / platform control A cpu all

================================================================
USER INTENT TO COMMAND MAPPINGS (OPERATIONAL)
================================================================

Hardware/System Information

"chassis type" → show version
"hardware info" → show version
"system info" → show version
"serial number" → show version
"software version" → show version

Interface Information

"interfaces" → show interface all
"interface status" → show interface all
"interface details" → show interface all detail
"interface stats" → show interface ethernet-1/X detail
"subinterface" → show interface ethernet-1/X subinterface 0

Routing Information

"routing table" → show network-instance default route-table
"routes" → show network-instance default route-table
"ipv4 routes" → show network-instance default route-table ipv4-unicast summary
"ipv6 routes" → show network-instance default route-table ipv6-unicast summary

BGP Information

"BGP" / "bgp summary" / "bgp status" → show network-instance default protocols bgp summary
"BGP neighbors" / "bgp peers" → show network-instance default protocols bgp neighbor
"BGP neighbor X.X.X.X" → show network-instance default protocols bgp neighbor X.X.X.X
"BGP routes" / "bgp prefixes" → show network-instance default protocols bgp routes ipv4 summary
"received routes from X.X.X.X" → show network-instance default protocols bgp neighbor X.X.X.X received-routes ipv4
"advertised routes to X.X.X.X" → show network-instance default protocols bgp neighbor X.X.X.X advertised-routes ipv4
"BGP configuration" / "show bgp config" → info network-instance default protocols bgp
"BGP peer groups" / "peer-group" → show network-instance default protocols bgp group
"BGP details" / "modify BGP" / "BGP settings" → info network-instance default protocols bgp (show config first, then discuss changes)

OSPF Information

"OSPF" / "ospf summary" / "ospf status" → show network-instance default protocols ospf status
"OSPF neighbors" → show network-instance default protocols ospf neighbor
"OSPF interfaces" → show network-instance default protocols ospf interface
"OSPF database" / "LSDB" → show network-instance default protocols ospf database
"OSPF routes" → show network-instance default protocols ospf routes
"OSPF configuration" / "show ospf config" → info network-instance default protocols ospf

Static Routes

"static routes" → show network-instance default static-routes
"static route configuration" → info network-instance default static-routes

LLDP Information

"neighbors" → show system lldp neighbor
"LLDP neighbors" → show system lldp neighbor

Network Instance / VRF Information

"VRFs" / "network instances" / "VRF list" → show network-instance summary
"VRF VRFNAME routes" → show network-instance VRFNAME route-table
"VRF configuration" → info network-instance VRFNAME

VLAN / Bridge Domain Information

"VLANs" / "bridge domain" / "MAC-VRF" → info network-instance MACVRFNAME
"MAC table" / "mac addresses" → show network-instance MACVRFNAME bridge-table mac-table all

Routing Policy Information

"routing policies" / "route policies" → info routing-policy
"policy POLICYNAME" → info routing-policy policy POLICYNAME
"prefix sets" / "prefix lists" → info routing-policy prefix-set PREFIXSETNAME

ACL Information

"ACLs" / "access lists" / "filters" → info acl
"ACL FILTERNAME" / "filter FILTERNAME" → info acl ipv4-filter FILTERNAME

System Services

"NTP" / "NTP status" → show system ntp
"NTP configuration" → info system ntp
"DNS" / "DNS configuration" → info system dns
"logging" / "syslog" → info system logging

BFD Information

"BFD" / "BFD sessions" → show bfd session
"BFD session X.X.X.X" → show bfd session peer X.X.X.X
"BFD configuration" → info bfd

Configuration Viewing

"configuration" → info system
"full configuration" → info
"running config" → info

================================================================
CONFIGURATION COMMANDS (set / delete)
================================================================

SR Linux Configuration Requirements
Always requires candidate mode and explicit commit:
enter candidate
set / ...
commit now

Interface Configuration

"enable interface X" →
set / interface X admin-state enable

"disable interface X" →
set / interface X admin-state disable

"configure interface X with IP Y" →
set / interface X admin-state enable
set / interface X subinterface 0 admin-state enable
set / interface X subinterface 0 ipv4 admin-state enable
set / interface X subinterface 0 ipv4 address Y
set / network-instance default interface X.0

"set interface description" →
set / interface X description "DESCRIPTION TEXT"

"set MTU on interface X" →
set / interface X mtu VALUE

"configure IPv6 on interface X" →
set / interface X subinterface 0 ipv6 admin-state enable
set / interface X subinterface 0 ipv6 address IPV6ADDR/PREFIX

LLDP Configuration

"enable LLDP on interface X" →
set / interface X admin-state enable
set / system lldp interface X admin-state enable

After LLDP configuration, always verify: info system lldp

================================================================
BGP CONFIGURATION
================================================================

CONFIGURE eBGP NEIGHBOR:
"configure BGP neighbor X.X.X.X with AS Y" / "add BGP peer" →
set / network-instance default protocols bgp autonomous-system LOCAL_AS
set / network-instance default protocols bgp router-id ROUTER_ID
set / network-instance default protocols bgp group GROUPNAME peer-as Y
set / network-instance default protocols bgp group GROUPNAME export-policy POLICYNAME
set / network-instance default protocols bgp group GROUPNAME import-policy POLICYNAME
set / network-instance default protocols bgp neighbor X.X.X.X peer-group GROUPNAME

If no group name specified, use "ebgp-peers" as default group name.
If no policies specified, create a default accept policy first.

CONFIGURE iBGP NEIGHBOR:
"configure iBGP with X.X.X.X" →
set / network-instance default protocols bgp autonomous-system LOCAL_AS
set / network-instance default protocols bgp router-id ROUTER_ID
set / network-instance default protocols bgp group ibgp-peers peer-as LOCAL_AS
set / network-instance default protocols bgp group ibgp-peers export-policy POLICYNAME
set / network-instance default protocols bgp group ibgp-peers import-policy POLICYNAME
set / network-instance default protocols bgp neighbor X.X.X.X peer-group ibgp-peers

SET BGP LOCAL AS:
"set BGP AS number" / "change autonomous system" →
set / network-instance default protocols bgp autonomous-system AS_NUMBER

SET BGP ROUTER-ID:
"set BGP router ID" →
set / network-instance default protocols bgp router-id X.X.X.X

CONFIGURE BGP PEER GROUP:
"create BGP peer group" / "add peer group" →
set / network-instance default protocols bgp group GROUPNAME peer-as AS_NUMBER
set / network-instance default protocols bgp group GROUPNAME export-policy POLICYNAME
set / network-instance default protocols bgp group GROUPNAME import-policy POLICYNAME

ADD BGP ADDRESS FAMILY:
"enable IPv4 unicast" / "add address family" →
set / network-instance default protocols bgp afi-safi ipv4-unicast admin-state enable
set / network-instance default protocols bgp group GROUPNAME afi-safi ipv4-unicast admin-state enable

"enable IPv6 unicast" →
set / network-instance default protocols bgp afi-safi ipv6-unicast admin-state enable
set / network-instance default protocols bgp group GROUPNAME afi-safi ipv6-unicast admin-state enable

"enable EVPN" →
set / network-instance default protocols bgp afi-safi evpn admin-state enable
set / network-instance default protocols bgp group GROUPNAME afi-safi evpn admin-state enable

DISABLE/REMOVE BGP NEIGHBOR:
"disable BGP neighbor X.X.X.X" →
set / network-instance default protocols bgp neighbor X.X.X.X admin-state disable

"remove BGP neighbor X.X.X.X" →
delete / network-instance default protocols bgp neighbor X.X.X.X

REMOVE BGP PEER GROUP:
"delete peer group GROUPNAME" →
delete / network-instance default protocols bgp group GROUPNAME

SET BGP TIMERS:
"set BGP hold timer" / "change keepalive" →
set / network-instance default protocols bgp group GROUPNAME timers hold-time SECONDS
set / network-instance default protocols bgp group GROUPNAME timers keepalive-interval SECONDS

SET BGP AUTHENTICATION:
"set BGP MD5 password" →
set / network-instance default protocols bgp neighbor X.X.X.X transport tcp-mss VALUE
set / network-instance default protocols bgp neighbor X.X.X.X auth-password PASSWORD

BGP IN VRF:
"configure BGP in VRF VRFNAME" →
set / network-instance VRFNAME protocols bgp autonomous-system AS_NUMBER
set / network-instance VRFNAME protocols bgp router-id ROUTER_ID
set / network-instance VRFNAME protocols bgp group GROUPNAME peer-as AS_NUMBER
set / network-instance VRFNAME protocols bgp neighbor X.X.X.X peer-group GROUPNAME

VERIFY after BGP changes:
show network-instance default protocols bgp summary
show network-instance default protocols bgp neighbor

================================================================
OSPF CONFIGURATION
================================================================

ENABLE OSPF:
"enable OSPF" / "configure OSPF" →
set / network-instance default protocols ospf instance main admin-state enable
set / network-instance default protocols ospf instance main router-id X.X.X.X
set / network-instance default protocols ospf instance main version ospf-v2

ADD OSPF AREA:
"add OSPF area" / "configure area X" →
set / network-instance default protocols ospf instance main area AREA_ID

ADD INTERFACE TO OSPF:
"add interface X to OSPF" / "enable OSPF on interface X" →
set / network-instance default protocols ospf instance main area AREA_ID interface X.0

Note: The interface must have an IP address and be in the network-instance before adding to OSPF.

SET OSPF INTERFACE TYPE:
"set OSPF interface type point-to-point" →
set / network-instance default protocols ospf instance main area AREA_ID interface X.0 interface-type point-to-point

Valid types: broadcast, point-to-point

SET OSPF PASSIVE INTERFACE:
"set passive interface" / "make interface passive" →
set / network-instance default protocols ospf instance main area AREA_ID interface X.0 passive true

SET OSPF COST/METRIC:
"set OSPF cost on interface" →
set / network-instance default protocols ospf instance main area AREA_ID interface X.0 metric VALUE

SET OSPF TIMERS:
"set OSPF hello interval" →
set / network-instance default protocols ospf instance main area AREA_ID interface X.0 hello-interval SECONDS
set / network-instance default protocols ospf instance main area AREA_ID interface X.0 dead-interval SECONDS

CONFIGURE OSPF STUB AREA:
"make area X a stub area" →
set / network-instance default protocols ospf instance main area AREA_ID stub

CONFIGURE OSPF NSSA:
"make area X an NSSA" →
set / network-instance default protocols ospf instance main area AREA_ID nssa

REMOVE INTERFACE FROM OSPF:
"remove interface from OSPF" →
delete / network-instance default protocols ospf instance main area AREA_ID interface X.0

DISABLE OSPF:
"disable OSPF" →
set / network-instance default protocols ospf instance main admin-state disable

OSPF IN VRF:
"configure OSPF in VRF VRFNAME" →
set / network-instance VRFNAME protocols ospf instance main admin-state enable
set / network-instance VRFNAME protocols ospf instance main router-id X.X.X.X
set / network-instance VRFNAME protocols ospf instance main area AREA_ID interface X.0

VERIFY after OSPF changes:
show network-instance default protocols ospf neighbor
show network-instance default protocols ospf interface

================================================================
STATIC ROUTE CONFIGURATION
================================================================

ADD STATIC ROUTE:
"add static route" / "create static route to NETWORK via NEXTHOP" →
set / network-instance default static-routes route PREFIX/LEN next-hop-group NHGNAME
set / network-instance default next-hop-groups group NHGNAME nexthop 1 ip-address NEXTHOP

ADD DEFAULT ROUTE:
"add default route" / "set default gateway" →
set / network-instance default static-routes route 0.0.0.0/0 next-hop-group default-nhg
set / network-instance default next-hop-groups group default-nhg nexthop 1 ip-address GATEWAY_IP

SET ROUTE PREFERENCE/METRIC:
"set route preference" / "set admin distance" →
set / network-instance default static-routes route PREFIX/LEN preference VALUE

REMOVE STATIC ROUTE:
"remove static route" / "delete route to NETWORK" →
delete / network-instance default static-routes route PREFIX/LEN

STATIC ROUTE IN VRF:
"add static route in VRF" →
set / network-instance VRFNAME static-routes route PREFIX/LEN next-hop-group NHGNAME
set / network-instance VRFNAME next-hop-groups group NHGNAME nexthop 1 ip-address NEXTHOP

VERIFY after static route changes:
show network-instance default static-routes
show network-instance default route-table

================================================================
NETWORK INSTANCE / VRF CONFIGURATION
================================================================

CREATE IP-VRF:
"create VRF" / "add network instance" →
set / network-instance VRFNAME type ip-vrf
set / network-instance VRFNAME admin-state enable
set / network-instance VRFNAME description "VRF description"

ADD INTERFACE TO VRF:
"add interface to VRF" →
set / network-instance VRFNAME interface X.SUBIF

Note: Interface must NOT be in another network-instance. Remove it first if needed.

CREATE MAC-VRF (bridge domain):
"create bridge domain" / "create MAC-VRF" →
set / network-instance MACVRFNAME type mac-vrf
set / network-instance MACVRFNAME admin-state enable

DELETE VRF:
"delete VRF" / "remove network instance" →
delete / network-instance VRFNAME

CONFIGURE ROUTE DISTINGUISHER/TARGET (for BGP EVPN):
"set route distinguisher" →
set / network-instance VRFNAME protocols bgp-vpn bgp-instance 1 route-distinguisher rd RD_VALUE
set / network-instance VRFNAME protocols bgp-vpn bgp-instance 1 route-target export-rt RT_VALUE
set / network-instance VRFNAME protocols bgp-vpn bgp-instance 1 route-target import-rt RT_VALUE

VERIFY:
show network-instance summary
info network-instance VRFNAME

================================================================
VLAN / SUBINTERFACE CONFIGURATION
================================================================

CONFIGURE VLAN TAGGING ON INTERFACE:
"configure VLAN X on interface Y" / "tag interface with VLAN" →
set / interface Y subinterface SUBID vlan encap single-tagged vlan-id VLAN_ID
set / interface Y subinterface SUBID admin-state enable

ACCESS PORT (untagged):
"configure access port" →
set / interface Y subinterface 0 vlan encap untagged
set / interface Y subinterface 0 admin-state enable

TRUNK PORT (multiple VLANs):
Configure separate subinterfaces for each VLAN:
set / interface Y subinterface 10 vlan encap single-tagged vlan-id 10
set / interface Y subinterface 10 admin-state enable
set / interface Y subinterface 20 vlan encap single-tagged vlan-id 20
set / interface Y subinterface 20 admin-state enable

ADD VLAN SUBINTERFACE TO BRIDGE DOMAIN:
"add VLAN to bridge domain" →
set / network-instance MACVRFNAME interface Y.SUBID

REMOVE VLAN:
"remove VLAN X from interface Y" →
delete / interface Y subinterface SUBID

VERIFY:
info interface Y subinterface SUBID
show network-instance MACVRFNAME bridge-table mac-table all

================================================================
ROUTING POLICY CONFIGURATION
================================================================

CREATE PREFIX SET:
"create prefix set" / "define prefix list" →
set / routing-policy prefix-set PREFIXSETNAME prefix PREFIX/LEN mask-length-range EXACT_OR_RANGE

Examples:
set / routing-policy prefix-set my-prefixes prefix 10.0.0.0/8 mask-length-range 8..32
set / routing-policy prefix-set default-only prefix 0.0.0.0/0 mask-length-range exact

CREATE COMMUNITY SET:
"create community set" →
set / routing-policy community-set COMMUNITYNAME member ["TARGET:VALUE"]

CREATE ROUTING POLICY:
"create routing policy" / "create route map" →
set / routing-policy policy POLICYNAME statement STMTNUM match prefix-set PREFIXSETNAME
set / routing-policy policy POLICYNAME statement STMTNUM action policy-result accept

"create reject policy" →
set / routing-policy policy POLICYNAME statement STMTNUM action policy-result reject

SET POLICY WITH LOCAL PREFERENCE:
"set local preference in policy" →
set / routing-policy policy POLICYNAME statement STMTNUM action bgp local-preference set VALUE

SET POLICY WITH MED:
"set MED in policy" →
set / routing-policy policy POLICYNAME statement STMTNUM action bgp med set VALUE

SET POLICY WITH COMMUNITY:
"set community in policy" →
set / routing-policy policy POLICYNAME statement STMTNUM action bgp communities add COMMUNITYNAME

DEFAULT ACCEPT-ALL POLICY:
"create accept all policy" →
set / routing-policy policy accept-all statement 10 action policy-result accept

DEFAULT REJECT-ALL POLICY:
"create reject all policy" →
set / routing-policy policy reject-all statement 10 action policy-result reject

DELETE POLICY:
"delete routing policy" →
delete / routing-policy policy POLICYNAME

APPLY POLICY TO BGP:
"apply import policy to BGP group" →
set / network-instance default protocols bgp group GROUPNAME import-policy POLICYNAME

"apply export policy to BGP group" →
set / network-instance default protocols bgp group GROUPNAME export-policy POLICYNAME

VERIFY:
info routing-policy policy POLICYNAME

================================================================
ACL / FILTER CONFIGURATION
================================================================

CREATE IPv4 ACL:
"create ACL" / "create access list" / "create filter" →
set / acl ipv4-filter FILTERNAME entry ENTRYNUM match source-ip prefix PREFIX/LEN
set / acl ipv4-filter FILTERNAME entry ENTRYNUM match destination-ip prefix PREFIX/LEN
set / acl ipv4-filter FILTERNAME entry ENTRYNUM match protocol VALUE
set / acl ipv4-filter FILTERNAME entry ENTRYNUM action accept

Protocol values: tcp, udp, icmp, or numeric (6=tcp, 17=udp, 1=icmp)

"create deny rule" →
set / acl ipv4-filter FILTERNAME entry ENTRYNUM action drop

MATCH TCP/UDP PORT:
"filter by port" →
set / acl ipv4-filter FILTERNAME entry ENTRYNUM match destination-port value VALUE
set / acl ipv4-filter FILTERNAME entry ENTRYNUM match source-port value VALUE

CREATE IPv6 ACL:
"create IPv6 ACL" →
set / acl ipv6-filter FILTERNAME entry ENTRYNUM match source-ip prefix PREFIX/LEN
set / acl ipv6-filter FILTERNAME entry ENTRYNUM action accept

APPLY ACL TO INTERFACE:
"apply ACL to interface" / "attach filter" →
set / acl interface X.0 input ipv4-filter FILTERNAME
set / acl interface X.0 output ipv4-filter FILTERNAME

REMOVE ACL FROM INTERFACE:
"remove ACL from interface" →
delete / acl interface X.0 input ipv4-filter FILTERNAME

DELETE ACL:
"delete ACL" / "remove filter" →
delete / acl ipv4-filter FILTERNAME

VERIFY:
info acl ipv4-filter FILTERNAME
show acl ipv4-filter FILTERNAME entry *

================================================================
SYSTEM SERVICES CONFIGURATION
================================================================

NTP CONFIGURATION:
"configure NTP" / "add NTP server" →
set / system ntp admin-state enable
set / system ntp server NTP_SERVER_IP

"remove NTP server" →
delete / system ntp server NTP_SERVER_IP

DNS CONFIGURATION:
"configure DNS" / "set DNS server" →
set / system dns network-instance default
set / system dns server-list [DNS_SERVER_IP]

LOGGING CONFIGURATION:
"configure syslog" / "set remote syslog" →
set / system logging network-instance default
set / system logging remote-server SYSLOG_SERVER_IP transport udp port 514

BANNER CONFIGURATION:
"set login banner" / "configure banner" →
set / system banner login-banner "BANNER TEXT"

HOSTNAME:
"set hostname" / "change device name" →
set / system name HOSTNAME

VERIFY:
info system ntp
info system dns
show system ntp

================================================================
BFD CONFIGURATION
================================================================

ENABLE BFD ON BGP NEIGHBOR:
"enable BFD for BGP" / "add BFD to BGP peer" →
set / bfd subinterface X.0 admin-state enable
set / bfd subinterface X.0 desired-minimum-transmit-interval MICROSECONDS
set / bfd subinterface X.0 required-minimum-receive MICROSECONDS
set / bfd subinterface X.0 detection-multiplier VALUE
set / network-instance default protocols bgp group GROUPNAME failure-detection enable-bfd true

ENABLE BFD ON OSPF INTERFACE:
"enable BFD for OSPF" →
set / bfd subinterface X.0 admin-state enable
set / network-instance default protocols ospf instance main area AREA_ID interface X.0 failure-detection enable-bfd true

VERIFY:
show bfd session

================================================================
CONFIGURATION MODE HANDLING
================================================================

SR Linux Requirements

Must enter candidate mode before configuration using: enter candidate
Must commit explicitly with: commit now
Interface must be enabled before configuring services
Leafref validation ensures referenced interfaces exist

Critical Prerequisites

Interface Prerequisites
- Interface must be admin-enabled before configuring services
- Must use candidate mode for all configuration changes
- Must commit changes explicitly
- Leafref validation ensures referenced interfaces exist

BGP Prerequisites
- Router-id must be set before adding neighbors
- Autonomous-system must be configured
- Peer group should have export-policy and import-policy
- Interface must have IP address and be in the network-instance

OSPF Prerequisites
- Router-id must be set
- Area must exist before adding interfaces
- Interface must have IP address and be in the network-instance

================================================================
ERROR PATTERNS AND TROUBLESHOOTING
================================================================

Common SR Linux Errors:

Leafref destination does not exist → Interface not enabled/configured
Unknown token 'set' → Not in candidate mode
Candidate is not empty → Previous configuration session pending
Could not find policy → Routing policy does not exist (create it first)
peer-group not found → BGP peer group not created yet
area not found → OSPF area not created yet

Solutions:

Enable interface first: set / interface X admin-state enable
Enter candidate mode: enter candidate
Commit pending changes: commit now
Clear candidate: discard now (WARNING: discards all uncommitted changes)

================================================================
EXAMPLES
================================================================

BGP eBGP Configuration Example:
User: "Configure BGP with AS 65001, add eBGP neighbor 10.0.0.2 AS 65002 on node-l1"
Commands executed:
1. enter candidate
2. set / network-instance default protocols bgp autonomous-system 65001
3. set / network-instance default protocols bgp router-id 10.0.0.1
4. set / routing-policy policy accept-all statement 10 action policy-result accept
5. set / network-instance default protocols bgp group ebgp-peers peer-as 65002
6. set / network-instance default protocols bgp group ebgp-peers export-policy accept-all
7. set / network-instance default protocols bgp group ebgp-peers import-policy accept-all
8. set / network-instance default protocols bgp neighbor 10.0.0.2 peer-group ebgp-peers
9. commit now
Verify: show network-instance default protocols bgp summary

OSPF Configuration Example:
User: "Enable OSPF on node-l1, add ethernet-1/1 to area 0"
Commands executed:
1. enter candidate
2. set / network-instance default protocols ospf instance main admin-state enable
3. set / network-instance default protocols ospf instance main router-id 10.0.0.1
4. set / network-instance default protocols ospf instance main version ospf-v2
5. set / network-instance default protocols ospf instance main area 0.0.0.0
6. set / network-instance default protocols ospf instance main area 0.0.0.0 interface ethernet-1/1.0
7. commit now
Verify: show network-instance default protocols ospf neighbor

Static Route Example:
User: "Add a default route via 10.0.0.1 on node-l1"
Commands executed:
1. enter candidate
2. set / network-instance default next-hop-groups group default-nhg nexthop 1 ip-address 10.0.0.1
3. set / network-instance default static-routes route 0.0.0.0/0 next-hop-group default-nhg
4. commit now
Verify: show network-instance default route-table

VRF Configuration Example:
User: "Create VRF 'customer-a' and add ethernet-1/2.0 to it"
Commands executed:
1. enter candidate
2. set / network-instance customer-a type ip-vrf
3. set / network-instance customer-a admin-state enable
4. set / network-instance customer-a description "Customer A VRF"
5. set / network-instance customer-a interface ethernet-1/2.0
6. commit now
Verify: show network-instance summary

VLAN Configuration Example:
User: "Configure VLAN 100 on ethernet-1/3 on node-l1"
Commands executed:
1. enter candidate
2. set / interface ethernet-1/3 subinterface 100 vlan encap single-tagged vlan-id 100
3. set / interface ethernet-1/3 subinterface 100 admin-state enable
4. commit now
Verify: info interface ethernet-1/3 subinterface 100

ACL Configuration Example:
User: "Create an ACL to permit traffic from 10.0.0.0/24 and deny everything else"
Commands executed:
1. enter candidate
2. set / acl ipv4-filter my-filter entry 10 match source-ip prefix 10.0.0.0/24
3. set / acl ipv4-filter my-filter entry 10 action accept
4. set / acl ipv4-filter my-filter entry 999 action drop
5. commit now
Verify: info acl ipv4-filter my-filter

Routing Policy Example:
User: "Create a policy to set local-preference 200 for routes from prefix 10.0.0.0/8"
Commands executed:
1. enter candidate
2. set / routing-policy prefix-set my-prefixes prefix 10.0.0.0/8 mask-length-range 8..32
3. set / routing-policy policy prefer-local statement 10 match prefix-set my-prefixes
4. set / routing-policy policy prefer-local statement 10 action policy-result accept
5. set / routing-policy policy prefer-local statement 10 action bgp local-preference set 200
6. set / routing-policy policy prefer-local statement 20 action policy-result accept
7. commit now
Verify: info routing-policy policy prefer-local

LLDP Configuration Example:
User: "Enable LLDP on ethernet-1/3 on node-l2"
Commands executed:
1. enter candidate
2. set / interface ethernet-1/3 admin-state enable
3. set / system lldp interface ethernet-1/3 admin-state enable
4. commit now
Verify: info system lldp

Interface IP Configuration Example:
User: "Configure interface ethernet-1/1 with IP 192.168.1.1/24 on node-l1"
Commands executed:
1. enter candidate
2. set / interface ethernet-1/1 admin-state enable
3. set / interface ethernet-1/1 subinterface 0 admin-state enable
4. set / interface ethernet-1/1 subinterface 0 ipv4 admin-state enable
5. set / interface ethernet-1/1 subinterface 0 ipv4 address 192.168.1.1/24
6. set / network-instance default interface ethernet-1/1.0
7. commit now
Verify: show interface ethernet-1/1

Operational Command Example:
User: "Check interface status on node-l1"
Command executed:
1. show interface all

================================================================
NATURAL LANGUAGE PROCESSING RULES
================================================================

Intent Mapping Workflow:
1. Parse user intent for operation type (show vs configure)
2. Map to SR Linux commands using the sections above
3. Handle prerequisites (interface enabling, policy creation)
4. Manage configuration mode (candidate mode)
5. Execute in proper sequence with error handling

Multi-Step Configuration Workflow:
For complex configurations:
1. Enter candidate mode
2. Create dependencies first (policies, prefix-sets, interfaces)
3. Configure main feature (BGP, OSPF, etc.)
4. Commit all changes together
5. Verify configuration applied

IMPORTANT: When user asks to "modify" or "change" a feature (BGP, OSPF, etc.):
1. FIRST show the current configuration using the appropriate info command
2. Present the current settings to the user
3. Ask what specific changes they want to make
4. Apply only the requested changes

================================================================
SSH CONNECTION BEHAVIOR (CRITICAL FOR PLAYBOOKS)
================================================================

When connecting via SSH as user "admin", you land directly in the SR Linux CLI, NOT in bash.
- DO NOT use sr_cli in playbooks — it is a bash utility, not a CLI command
- Send commands directly: show version, info system, enter candidate, etc.
- sr_cli is ONLY valid when SSH lands in bash (e.g., linuxadmin user)

WRONG (will fail with "Unknown token 'sr_cli'"):
  ansible.builtin.raw: "sr_cli 'show version'"

CORRECT (direct CLI command):
  ansible.builtin.raw: "show version"

---
Ansible Playbook Example - Show Version
- name: Nokia SR Linux - Show Version
  hosts: all
  gather_facts: false
  vars:
    ansible_connection: ssh
    ansible_ssh_common_args: "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"

  tasks:
    - name: Get version via raw SSH
      ansible.builtin.raw: "show version"
      register: output
      changed_when: false

    - name: Show result
      ansible.builtin.debug:
        msg: "{{ output.stdout_lines }}"
