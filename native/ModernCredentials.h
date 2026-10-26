#pragma once
#include <cstdint>
namespace c500g {
// Account IDs cross Java/.NET as signed 64-bit values; SDK storage is uint64_t.
inline uint64_t SdkAccessId(int64_t id) { return static_cast<uint64_t>(id); }
inline int ValidateCredentials(int64_t id, const uint8_t* token, int length) {
 // Reject only the missing value. A negative signed ID can represent a valid uint64_t.
 if(id==0)return -1101;
 if(!token || length<128 || length>4096 || (length&1))return -1102;
 for(int i=0;i<length;i++){
  uint8_t c=token[i];
  if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F')))return -1103;
 }
 return 0;
}
}
